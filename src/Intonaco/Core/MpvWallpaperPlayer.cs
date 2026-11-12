using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Intonaco.Core
{
    /// <summary>
    /// Opt-in MPV desktop renderer. No libmpv DLL or codec assets are
    /// distributed with Intonaco: the user supplies an existing mpv.exe.
    /// Embeds into an HWND owned by the existing WorkerW player window.
    /// IPC runs on an async named pipe. The UI thread is never blocked
    /// waiting for media frames or the MPV process.
    /// </summary>
    public sealed class MpvWallpaperPlayer : IWallpaperPlayer
    {
        private readonly Dispatcher _dispatcher;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private MpvWindowHost? _host;
        private Process? _process;
        private CancellationTokenSource? _session;
        private NamedPipeClientStream? _pipe;
        private StreamWriter? _writer;
        private string? _filePath;
        private bool _disposed;
        private bool _opened;
        private bool _errorReported;
        private long _positionTimestamp;
        private double _positionSeconds;
        private bool _isMuted = true;
        private double _volume;
        private int _targetFps = 30;
        private Stretch _stretch = Stretch.UniformToFill;
        private double _speed = 1.0;
        private bool _loop = true;

        public event Action<string>? ErrorOccurred;
        public event Action<WallpaperPlayer.PlaybackState>? StateChanged;
        public event Action<TimeSpan>? MediaOpened;

        public MpvWallpaperPlayer()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _positionTimestamp = Stopwatch.GetTimestamp();
        }

        public static bool TryLocateExecutable(out string executable)
        {
            string? fromEnvironment = Environment.GetEnvironmentVariable("INTONACO_MPV");
            if (IsValidExecutable(fromEnvironment))
            {
                executable = Path.GetFullPath(fromEnvironment!);
                return true;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "mpv.exe");
            if (IsValidExecutable(local))
            {
                executable = local;
                return true;
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (path != null)
            {
                foreach (string entry in path.Split(Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string candidate = Path.Combine(entry.Trim().Trim('"'), "mpv.exe");
                        if (!IsValidExecutable(candidate)) continue;
                        executable = Path.GetFullPath(candidate);
                        return true;
                    }
                    catch (Exception ex) when (
                        ex is ArgumentException or NotSupportedException
                           or PathTooLongException)
                    {
                        // Ignore one invalid PATH directory.
                    }
                }
            }

            executable = "";
            return false;
        }

        private static bool IsValidExecutable(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }
            catch { return false; }
        }

        public WallpaperPlayer.PlaybackState CurrentState { get; private set; } =
            WallpaperPlayer.PlaybackState.Stopped;
        public bool IsMuted => _isMuted;
        public string? CurrentFilePath => _filePath;
        public int NaturalVideoWidth { get; private set; }
        public int NaturalVideoHeight { get; private set; }
        public TimeSpan Duration { get; private set; }

        public TimeSpan Position
        {
            get
            {
                double seconds = _positionSeconds;
                if (CurrentState == WallpaperPlayer.PlaybackState.Playing)
                {
                    seconds += (Stopwatch.GetTimestamp() - _positionTimestamp) /
                        (double)Stopwatch.Frequency * _speed;
                }
                if (Duration > TimeSpan.Zero)
                    seconds = _loop
                        ? seconds % Duration.TotalSeconds
                        : Math.Min(seconds, Duration.TotalSeconds);

                return TimeSpan.FromSeconds(Math.Max(0, seconds));
            }
        }

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
                Send("set_property", "volume", _volume * 100);
            }
        }

        /// <summary>
        /// MPV uses the source frame cadence. We do not pretend a UI slider
        /// can impose a zero-cost FPS cap on MPV's GPU pipeline.
        /// </summary>
        public int TargetFps
        {
            get => _targetFps;
            set => _targetFps = Math.Clamp(value, 1, 60);
        }

        public Stretch StretchMode
        {
            get => _stretch;
            set
            {
                _stretch = value == Stretch.Uniform ? Stretch.Uniform :
                    Stretch.UniformToFill;
                Send("set_property", "panscan",
                    _stretch == Stretch.Uniform ? 0.0 : 1.0);
            }
        }

        public double PlaybackSpeed
        {
            get => _speed;
            set
            {
                SetPositionBase(Position);
                _speed = double.IsFinite(value) ? Math.Clamp(value, 0.25, 2) : 1;
                Send("set_property", "speed", _speed);
            }
        }

        public bool LoopPlayback
        {
            get => _loop;
            set
            {
                _loop = value;
                Send("set_property", "loop-file", value ? "inf" : "no");
            }
        }

        public FrameworkElement CreateHost()
        {
            if (_host != null)
                _host.Dispose();
            _host = new MpvWindowHost
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            return _host;
        }

        public bool Play(string filePath)
        {
            if (_disposed || !TryLocateExecutable(out string executable))
            {
                ErrorOccurred?.Invoke(
                    "MPV is not installed. Download mpv.exe from mpv.io, " +
                    "put it beside Intonaco.exe, or add it to PATH. " +
                    "Windows playback is still available.");
                return false;
            }

            if (!MediaFormatProbe.TryValidate(filePath, out string error))
            {
                ErrorOccurred?.Invoke(error);
                return false;
            }

            Stop();

            if (_host?.Handle == IntPtr.Zero || _host == null)
            {
                ErrorOccurred?.Invoke("Could not initialize the MPV desktop surface.");
                return false;
            }

            _opened = false;
            _errorReported = false;
            _filePath = Path.GetFullPath(filePath);
            _positionSeconds = 0;
            _positionTimestamp = Stopwatch.GetTimestamp();
            Duration = TimeSpan.Zero;
            NaturalVideoWidth = 0;
            NaturalVideoHeight = 0;

            string name = "intonaco-mpv-" + Guid.NewGuid().ToString("N");
            _session = new CancellationTokenSource();

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            string[] options =
            {
                "--no-config", "--no-terminal", "--no-border",
                "--no-osc", "--osd-level=0", "--force-window=yes",
                "--keep-open=yes", "--input-default-bindings=no",
                "--hwdec=auto-safe", "--vo=gpu", "--interpolation=no",
                "--demuxer-readahead-secs=2",
                "--demuxer-max-bytes=16MiB",
                "--wid=" + unchecked((uint)_host.Handle.ToInt64())
                    .ToString(CultureInfo.InvariantCulture),
                "--input-ipc-server=\\\\.\\pipe\\" + name,
                "--mute=" + (_isMuted ? "yes" : "no"),
                "--volume=" + (_volume * 100).ToString(CultureInfo.InvariantCulture),
                "--speed=" + _speed.ToString(CultureInfo.InvariantCulture),
                "--panscan=" + (_stretch == Stretch.Uniform ? "0" : "1"),
                "--loop-file=" + (_loop ? "inf" : "no"),
                _filePath
            };
            foreach (string option in options)
                start.ArgumentList.Add(option);

            try
            {
                var process = new Process { StartInfo = start };
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (!ReferenceEquals(process, _process)) return;
                        if (!_opened && !_errorReported)
                            ReportError("MPV exited before opening the wallpaper. " +
                                "Verify that your mpv.exe supports the video codec.");
                        else if (!_errorReported &&
                                 CurrentState != WallpaperPlayer.PlaybackState.Stopped)
                            ReportError("The MPV wallpaper process stopped unexpectedly.");
                    });
                };

                _process = process;
                if (!process.Start())
                    throw new InvalidOperationException("MPV failed to launch.");
                SetState(WallpaperPlayer.PlaybackState.Playing);
                _ = RunIpcAsync(name, _session.Token);
                return true;
            }
            catch (Exception ex)
            {
                Stop();
                ErrorOccurred?.Invoke("Could not launch MPV: " + ex.Message);
                return false;
            }
        }

        private async Task RunIpcAsync(string pipeName, CancellationToken cancel)
        {
            try
            {
                NamedPipeClientStream? client = null;
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (_process?.HasExited != false)
                        throw new IOException("MPV stopped before IPC was available.");

                    client?.Dispose();
                    client = new NamedPipeClientStream(".", pipeName,
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await client.ConnectAsync(150, cancel).ConfigureAwait(false);
                        break;
                    }
                    catch (TimeoutException)
                    {
                        await Task.Delay(50, cancel).ConfigureAwait(false);
                    }
                }

                if (client == null || !client.IsConnected)
                {
                    client?.Dispose();
                    throw new IOException("MPV IPC connection was unavailable.");
                }

                _pipe = client;
                _writer = new StreamWriter(client, new System.Text.UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                await SendAsync(new object[] { "observe_property", 1, "duration" });
                await SendAsync(new object[] { "observe_property", 2, "video-params" });
                await SendAsync(new object[] { "observe_property", 3, "pause" });
                await SendAsync(new object[] { "get_property", "duration" });
                await SendAsync(new object[] { "get_property", "video-params" });
                // The wallpaper can be paused by system focus policy before
                // the named pipe is ready; mirror state once connected.
                await SendAsync(new object[] { "set_property", "pause",
                    CurrentState == WallpaperPlayer.PlaybackState.Paused });

                using var reader = new StreamReader(client,
                    new System.Text.UTF8Encoding(false),
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true);

                while (!cancel.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(cancel)
                        .ConfigureAwait(false);
                    if (line == null)
                        break;
                    HandleMessage(line, cancel);
                }
            }
            catch (OperationCanceledException)
            {
                // User stopped playback or selected another wallpaper.
            }
            catch (Exception ex)
            {
                if (!cancel.IsCancellationRequested)
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (!cancel.IsCancellationRequested)
                            ReportError("MPV communication failed: " + ex.Message);
                    });
                }
            }
        }

        private void HandleMessage(string line, CancellationToken sessionToken)
        {
            if (sessionToken.IsCancellationRequested) return;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;

                string eventName = root.TryGetProperty("event", out var evt)
                    ? evt.GetString() ?? "" : "";

                if (eventName == "end-file" &&
                    root.TryGetProperty("reason", out var reason) &&
                    reason.GetString() == "error")
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (!sessionToken.IsCancellationRequested)
                            ReportError("MPV could not decode this wallpaper.");
                    });
                    return;
                }

                if (eventName == "file-loaded")
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (sessionToken.IsCancellationRequested ||
                            _filePath == null) return;
                        NotifyOpened();
                        if (_positionSeconds > 0)
                            Seek(TimeSpan.FromSeconds(_positionSeconds));
                        if (CurrentState == WallpaperPlayer.PlaybackState.Paused)
                            Send("set_property", "pause", true);
                    });
                    return;
                }
                if (eventName == "end-file" && !_loop &&
                    root.TryGetProperty("reason", out var endReason) &&
                    endReason.GetString() == "eof")
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (!sessionToken.IsCancellationRequested &&
                            CurrentState == WallpaperPlayer.PlaybackState.Playing)
                            Pause();
                    });
                    return;
                }

                JsonElement data;
                if (!root.TryGetProperty("data", out data))
                    return;

                string name = root.TryGetProperty("name", out var prop)
                    ? prop.GetString() ?? "" : "";
                bool response = root.TryGetProperty("request_id", out _);
                // get_property replies have no property name. Their purpose is
                // to encourage the observed initial values after connecting.
                if (response) return;

                if (name == "duration" && data.ValueKind == JsonValueKind.Number &&
                    data.TryGetDouble(out double seconds) && double.IsFinite(seconds))
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (sessionToken.IsCancellationRequested) return;
                        TimeSpan updated = TimeSpan.FromSeconds(Math.Max(0, seconds));
                        if (updated != Duration)
                        {
                            Duration = updated;
                            if (_opened) MediaOpened?.Invoke(Duration);
                        }
                        NotifyOpened();
                    });
                }
                else if (name == "video-params" &&
                         data.ValueKind == JsonValueKind.Object)
                {
                    int w = ReadInt(data, "w");
                    int h = ReadInt(data, "h");
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (sessionToken.IsCancellationRequested) return;
                        bool dimensionsChanged =
                            NaturalVideoWidth != w || NaturalVideoHeight != h;
                        NaturalVideoWidth = w;
                        NaturalVideoHeight = h;
                        if (dimensionsChanged && _opened)
                            MediaOpened?.Invoke(Duration);
                        NotifyOpened();
                    });
                }
            }
            catch (JsonException)
            {
                // Ignore one malformed response instead of stopping playback.
            }
            catch (InvalidOperationException)
            {
                // Event shape can differ across MPV builds.
            }
        }

        private static int ReadInt(JsonElement value, string name)
        {
            if (value.TryGetProperty(name, out var entry) &&
                entry.TryGetInt32(out int number))
                return number;
            return 0;
        }

        private void NotifyOpened()
        {
            if (_opened || _disposed ||
                CurrentState is WallpaperPlayer.PlaybackState.Stopped or
                    WallpaperPlayer.PlaybackState.Error)
                return;

            _opened = true;
            MediaOpened?.Invoke(Duration);
        }

        private void ReportError(string message)
        {
            if (_disposed || _errorReported ||
                CurrentState == WallpaperPlayer.PlaybackState.Stopped)
                return;

            _errorReported = true;
            SetState(WallpaperPlayer.PlaybackState.Error);
            ErrorOccurred?.Invoke(message);
        }

        private void Send(string property, string name, object value)
        {
            if (_disposed) return;
            _ = SendAsync(new object[] { property, name, value });
        }

        private async Task SendAsync(object[] command)
        {
            StreamWriter? writer = _writer;
            CancellationTokenSource? session = _session;
            if (writer == null || session == null ||
                session.IsCancellationRequested)
                return;

            try
            {
                await _sendLock.WaitAsync(session.Token).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(session, _session) ||
                        session.IsCancellationRequested)
                        return;

                    await writer.WriteLineAsync(
                        JsonSerializer.Serialize(new { command }))
                        .ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException
                                      or IOException or ObjectDisposedException
                                      or InvalidOperationException)
            {
                // Process shutdown races must not crash the WPF dispatcher.
            }
        }

        private void SetPositionBase(TimeSpan position)
        {
            _positionSeconds = Math.Max(0, position.TotalSeconds);
            _positionTimestamp = Stopwatch.GetTimestamp();
        }

        private void SetState(WallpaperPlayer.PlaybackState state)
        {
            if (CurrentState == state) return;
            CurrentState = state;
            StateChanged?.Invoke(state);
        }

        public void Pause()
        {
            if (CurrentState != WallpaperPlayer.PlaybackState.Playing) return;
            SetPositionBase(Position);
            SetState(WallpaperPlayer.PlaybackState.Paused);
            Send("set_property", "pause", true);
        }

        public void PauseAt(TimeSpan position)
        {
            Seek(position);
            if (CurrentState == WallpaperPlayer.PlaybackState.Playing)
                Pause();
        }

        public void Resume()
        {
            if (CurrentState != WallpaperPlayer.PlaybackState.Paused) return;
            _positionTimestamp = Stopwatch.GetTimestamp();
            SetState(WallpaperPlayer.PlaybackState.Playing);
            Send("set_property", "pause", false);
        }

        public void Seek(TimeSpan position)
        {
            double seconds = Math.Max(0, position.TotalSeconds);
            if (Duration > TimeSpan.Zero)
                seconds = Math.Min(seconds, Duration.TotalSeconds);
            SetPositionBase(TimeSpan.FromSeconds(seconds));
            if (_opened)
                _ = SendAsync(new object[] { "seek", seconds, "absolute+exact" });
            else
                _ = SendAsync(new object[] {
                    "set_property", "time-pos", seconds
                });
        }

        public void SetMute(bool muted)
        {
            _isMuted = muted;
            Send("set_property", "mute", muted);
        }

        public void ToggleMute() => SetMute(!_isMuted);

        public void Stop()
        {
            _session?.Cancel();
            _session?.Dispose();
            _session = null;
            _writer = null;
            _pipe?.Dispose();
            _pipe = null;

            var process = _process;
            _process = null;
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                finally { process.Dispose(); }
            }

            // Keep the HWND host until the WPF player window is unloaded.
            // Play() calls Stop() to replace a source, and disposing its
            // host here would make every first MPV playback fail.
            _filePath = null;
            _opened = false;
            _positionSeconds = 0;
            _positionTimestamp = Stopwatch.GetTimestamp();
            Duration = TimeSpan.Zero;
            NaturalVideoWidth = 0;
            NaturalVideoHeight = 0;
            SetState(WallpaperPlayer.PlaybackState.Stopped);
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _host?.Dispose();
            _host = null;
            _disposed = true;
            _sendLock.Dispose();
        }
    }
}
