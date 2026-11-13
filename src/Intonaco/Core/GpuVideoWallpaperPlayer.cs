using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Intonaco.Core
{
    /// <summary>
    /// GPU-first in-process wallpaper engine. The original native module uses
    /// Media Foundation's modern IMFMediaEngine with an actual D3D11 hardware
    /// device, a bounded two-buffer DXGI presentation queue and zero readbacks
    /// to System.Drawing/WPF bitmaps. No external application is started.
    ///
    /// Source resolution, range, color metadata and frame timing remain owned
    /// by Media Foundation; do not boost saturation or silently scale to 1080p.
    /// </summary>
    public sealed class GpuVideoWallpaperPlayer : IWallpaperPlayer
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Stats
        {
            public int Phase;
            public int ErrorHresult;
            public int GpuDecode;
            public int Width;
            public int Height;
            // Native 4K decoder has been shut down. Only the last full-quality
            // DXGI frame remains for the desktop wallpaper to display.
            public int DecoderSleeping;
            public double DurationSeconds;
            public double PositionSeconds;
            public long FramesPresented;
        }

        private static class Native
        {
            [DllImport("Intonaco.Video.dll", EntryPoint = "IntonacoVideo_Create",
                CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
            internal static extern int Create(IntPtr window,
                [MarshalAs(UnmanagedType.LPWStr)] string url, out IntPtr engine);

            [DllImport("Intonaco.Video.dll", EntryPoint = "IntonacoVideo_Configure",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern void Configure(IntPtr engine, int paused,
                int muted, double volume, double speed, int loop, int fill);

            [DllImport("Intonaco.Video.dll", EntryPoint = "IntonacoVideo_Seek",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern void Seek(IntPtr engine, double position);

            [DllImport("Intonaco.Video.dll", EntryPoint = "IntonacoVideo_GetStats",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern int GetStats(IntPtr engine, out Stats stats);

            [DllImport("Intonaco.Video.dll", EntryPoint = "IntonacoVideo_Destroy",
                CallingConvention = CallingConvention.Cdecl)]
            internal static extern void Destroy(IntPtr engine);
        }

        private IntPtr _engine;
        private NativeVideoWindowHost? _host;
        private readonly DispatcherTimer _poll;
        private bool _disposed;
        private bool _pauseRequested;
        private bool _isMuted = true;
        private bool _loop = true;
        private bool _fill = true;
        private bool _mediaOpened;
        private double _volume;
        private double _speed = 1;
        private int _targetFps = 30;
        private string? _file;
        private TimeSpan _position;
        private long _frames;
        private bool _errorRaised;
        private bool _lastDecoderSleeping;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

        public GpuVideoWallpaperPlayer()
        {
            _poll = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _poll.Tick += (_, _) => Poll();
        }

        public event Action<string>? ErrorOccurred;
        public event Action<WallpaperPlayer.PlaybackState>? StateChanged;
        public event Action<TimeSpan>? MediaOpened;

        public WallpaperPlayer.PlaybackState CurrentState { get; private set; } =
            WallpaperPlayer.PlaybackState.Stopped;

        public bool IsMuted => _isMuted;
        public string? CurrentFilePath => _file;
        public int NaturalVideoWidth { get; private set; }
        public int NaturalVideoHeight { get; private set; }
        public TimeSpan Duration { get; private set; }
        public TimeSpan Position => _position;

        public int TargetFps
        {
            get => _targetFps;
            set => _targetFps = Math.Clamp(value, 1, 60);
        }

        public double Volume
        {
            get => _volume;
            set { _volume = Math.Clamp(value, 0, 1); Configure(); }
        }

        public Stretch StretchMode
        {
            get => _fill ? Stretch.UniformToFill : Stretch.Uniform;
            set { _fill = value != Stretch.Uniform; Configure(); }
        }

        public double PlaybackSpeed
        {
            get => _speed;
            set { _speed = Math.Clamp(value, 0.25, 2); Configure(); }
        }

        public bool LoopPlayback
        {
            get => _loop;
            set { _loop = value; Configure(); }
        }

        public FrameworkElement CreateHost()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _host ??= new NativeVideoWindowHost();
        }

        public bool Play(string path)
        {
            if (_disposed)
            {
                ErrorOccurred?.Invoke("The GPU video engine has been disposed.");
                return false;
            }
            if (!MediaFormatProbe.TryValidate(path, out string failure))
            {
                ErrorOccurred?.Invoke(failure);
                return false;
            }

            Stop();
            if (_host?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
            {
                ErrorOccurred?.Invoke("The GPU video window is not ready.");
                return false;
            }

            try
            {
                string url = new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri;
                int result = Native.Create(hwnd, url, out IntPtr created);
                if (result < 0 || created == IntPtr.Zero)
                {
                    ErrorOccurred?.Invoke(
                        "GPU engine could not start (HRESULT 0x" +
                        unchecked((uint)result).ToString("X8") + ").");
                    return false;
                }
                _engine = created;
                _file = System.IO.Path.GetFullPath(path);
                _mediaOpened = false;
                _errorRaised = false;
                _frames = 0;
                Configure();
                _poll.Start();
                SetState(WallpaperPlayer.PlaybackState.Playing);
                DebugLog.Write("Intonaco GPU engine started: DXGI frame server with " +
                    "hardware decoder, two-buffer D3D11 compositor, no external player.");
                return true;
            }
            catch (DllNotFoundException)
            {
                ErrorOccurred?.Invoke("The bundled native GPU renderer is missing. " +
                    "Reinstall Intonaco or select Windows compatibility mode.");
            }
            catch (EntryPointNotFoundException)
            {
                ErrorOccurred?.Invoke("The bundled GPU renderer is incompatible. " +
                    "Reinstall Intonaco or select Windows compatibility mode.");
            }
            catch (BadImageFormatException)
            {
                ErrorOccurred?.Invoke("The GPU renderer has the wrong architecture.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                ErrorOccurred?.Invoke("GPU playback startup failed: " + ex.Message);
            }
            return false;
        }

        private void Configure()
        {
            if (_engine == IntPtr.Zero || _disposed)
                return;
            Native.Configure(_engine, _pauseRequested ? 1 : 0,
                _isMuted ? 1 : 0, _volume, _speed, _loop ? 1 : 0,
                _fill ? 1 : 0);
        }

        private void Poll()
        {
            if (_disposed || _engine == IntPtr.Zero)
                return;

            try
            {
                int result = Native.GetStats(_engine, out Stats s);
                if (result < 0)
                {
                    ReportError("GPU video status unavailable.");
                    return;
                }

                if (s.Phase < 0)
                {
                    ReportError("GPU video playback failed (HRESULT 0x" +
                        unchecked((uint)s.ErrorHresult).ToString("X8") +
                        "). Select Windows (fallback) if your GPU is unsupported.");
                    return;
                }

                bool decoderSleeping = s.DecoderSleeping != 0;
                if (decoderSleeping != _lastDecoderSleeping)
                {
                    _lastDecoderSleeping = decoderSleeping;
                    DebugLog.Write(decoderSleeping
                        ? "GPU deep pause active: decoder shut down, frozen DXGI frame retained."
                        : "GPU deep pause exited: video decoder restarted on resume.");
                }

                if (s.Width > 0 && s.Height > 0)
                {
                    NaturalVideoWidth = s.Width;
                    NaturalVideoHeight = s.Height;
                    if (s.DurationSeconds > 0 && double.IsFinite(s.DurationSeconds))
                        Duration = TimeSpan.FromSeconds(s.DurationSeconds);

                    if (!_mediaOpened)
                    {
                        _mediaOpened = true;
                        DebugLog.Write(
                            $"GPU video loaded: {s.Width}x{s.Height}, D3D11 hardware={s.GpuDecode != 0}, " +
                            $"frame count={s.FramesPresented}; native frame server and limited surface queue.");
                        MediaOpened?.Invoke(Duration);
                    }
                }

                if (double.IsFinite(s.PositionSeconds) && s.PositionSeconds >= 0)
                    _position = TimeSpan.FromSeconds(s.PositionSeconds);

                if (_frames != s.FramesPresented)
                    _frames = s.FramesPresented;

                RuntimeMemoryDiagnostics.LogIfDue(
                    "GPU-DirectX", false, s.Width, s.Height);
            }
            catch (DllNotFoundException)
            {
                ReportError("The Intonaco GPU renderer DLL is missing.");
            }
            catch (EntryPointNotFoundException)
            {
                ReportError("The Intonaco GPU renderer DLL is outdated.");
            }
            catch (SEHException ex)
            {
                ReportError("Native video graphics driver error: " + ex.Message);
            }
        }

        private void ReportError(string message)
        {
            if (_errorRaised) return;
            _errorRaised = true;
            DebugLog.Write("GPU renderer error: " + message);
            SetState(WallpaperPlayer.PlaybackState.Error);
            ErrorOccurred?.Invoke(message);
        }

        private void SetState(WallpaperPlayer.PlaybackState state)
        {
            if (CurrentState == state) return;
            CurrentState = state;
            StateChanged?.Invoke(state);
        }

        public void Pause()
        {
            if (CurrentState != WallpaperPlayer.PlaybackState.Playing)
                return;
            _pauseRequested = true;
            Configure();
            SetState(WallpaperPlayer.PlaybackState.Paused);
        }

        public void PauseAt(TimeSpan position)
        {
            Seek(position);
            Pause();
        }

        public void Resume()
        {
            if (CurrentState != WallpaperPlayer.PlaybackState.Paused)
                return;
            _pauseRequested = false;
            Configure();
            SetState(WallpaperPlayer.PlaybackState.Playing);
        }

        public void Seek(TimeSpan position)
        {
            _position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
            if (_engine != IntPtr.Zero)
                Native.Seek(_engine, _position.TotalSeconds);
        }

        public void SetMute(bool muted)
        {
            _isMuted = muted;
            Configure();
        }

        public void ToggleMute() => SetMute(!_isMuted);

        public void Stop()
        {
            _poll.Stop();
            if (_engine != IntPtr.Zero)
            {
                IntPtr handle = _engine;
                _engine = IntPtr.Zero;
                try { Native.Destroy(handle); }
                catch (DllNotFoundException ex)
                {
                    DebugLog.Write("GPU DLL unavailable during shutdown: " + ex.Message);
                }
            }
            _mediaOpened = false;
            _errorRaised = false;
            _lastDecoderSleeping = false;
            _pauseRequested = false;
            _position = TimeSpan.Zero;
            NaturalVideoWidth = NaturalVideoHeight = 0;
            Duration = TimeSpan.Zero;
            _file = null;
            SetState(WallpaperPlayer.PlaybackState.Stopped);
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _disposed = true;
            _poll.Stop();
            _host?.Dispose();
            _host = null;
        }
    }
}
