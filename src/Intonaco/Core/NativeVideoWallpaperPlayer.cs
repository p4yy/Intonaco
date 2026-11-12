using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Intonaco.Core
{
    /// <summary>
    /// Optional original Intonaco renderer. Native Media Foundation decodes into
    /// D3D11 textures and presents into a WorkerW child HWND. No 4K pixel
    /// buffers are marshalled into .NET, and no browser/MPV process is launched.
    ///
    /// Isolated as an opt-in test engine until we have genuine same-machine
    /// performance and compatibility measurements for real wallpaper files.
    /// </summary>
    public sealed class NativeVideoWallpaperPlayer : IWallpaperPlayer
    {
        private const string Library = "IntonacoVideo";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr iv_create(IntPtr hwnd);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void iv_destroy(IntPtr engine);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern void iv_open(IntPtr engine,
            [MarshalAs(UnmanagedType.LPWStr)] string path);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void iv_pause(IntPtr engine, int paused);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void iv_stop(IntPtr engine);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void iv_options(IntPtr engine, int cover,
            int loop, double speed, double volume);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern void iv_seek(IntPtr engine, double seconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int iv_state(IntPtr engine);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int iv_error(IntPtr engine);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int iv_width(IntPtr engine);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern int iv_height(IntPtr engine);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long iv_position_ms(IntPtr engine);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        private static extern long iv_duration_ms(IntPtr engine);

        private readonly DispatcherTimer _poll;
        private NativeVideoWindowHost? _host;
        private IntPtr _engine;
        private bool _disposed;
        private bool _opened;
        private bool _muted = true;
        private double _volume;
        private double _speed = 1.0;
        private bool _loop = true;
        private Stretch _stretch = Stretch.UniformToFill;
        private int _fps = 30;
        private string? _path;

        public static bool IsAvailable =>
            Environment.Is64BitProcess &&
            File.Exists(Path.Combine(AppContext.BaseDirectory, Library + ".dll"));

        public event Action<string>? ErrorOccurred;
        public event Action<WallpaperPlayer.PlaybackState>? StateChanged;
        public event Action<TimeSpan>? MediaOpened;

        public NativeVideoWallpaperPlayer()
        {
            _poll = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(450)
            };
            _poll.Tick += (_, _) => PollNative();
        }

        public WallpaperPlayer.PlaybackState CurrentState { get; private set; } =
            WallpaperPlayer.PlaybackState.Stopped;

        public bool IsMuted => _muted;
        public string? CurrentFilePath => _path;
        public int NaturalVideoWidth { get; private set; }
        public int NaturalVideoHeight { get; private set; }
        public TimeSpan Duration { get; private set; }
        public TimeSpan Position => _engine != IntPtr.Zero
            ? TimeSpan.FromMilliseconds(Math.Max(0, iv_position_ms(_engine)))
            : TimeSpan.Zero;

        public int TargetFps
        {
            get => _fps;
            // TargetFps is a UI preference; the native hardware decoder
            // follows the source rate. Never fake a frame-rate cap.
            set => _fps = Math.Clamp(value, 1, 60);
        }

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
                SendOptions();
            }
        }

        public Stretch StretchMode
        {
            get => _stretch;
            set
            {
                _stretch = value == Stretch.Uniform
                    ? Stretch.Uniform : Stretch.UniformToFill;
                SendOptions();
            }
        }

        public double PlaybackSpeed
        {
            get => _speed;
            set
            {
                _speed = double.IsFinite(value) ? Math.Clamp(value, 0.25, 2) : 1;
                SendOptions();
            }
        }

        public bool LoopPlayback
        {
            get => _loop;
            set
            {
                _loop = value;
                SendOptions();
            }
        }

        public FrameworkElement CreateHost()
        {
            _host?.Dispose();
            _host = new NativeVideoWindowHost
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            return _host;
        }

        public bool Play(string filePath)
        {
            if (_disposed) return false;
            if (!IsAvailable)
            {
                ErrorOccurred?.Invoke("The native GPU engine was not bundled. " +
                    "Select Windows in Video engine to continue.");
                return false;
            }
            if (!MediaFormatProbe.TryValidate(filePath, out string error))
            {
                ErrorOccurred?.Invoke(error);
                return false;
            }
            if (_host == null || _host.Handle == IntPtr.Zero)
            {
                ErrorOccurred?.Invoke("The native GPU wallpaper surface is not ready.");
                return false;
            }
            Stop();
            try
            {
                _path = Path.GetFullPath(filePath);
                _opened = false;
                _engine = iv_create(_host.Handle);
                if (_engine == IntPtr.Zero)
                    throw new InvalidOperationException(
                        "Could not initialize the native GPU rendering thread.");
                SendOptions();
                // MediaEngine expects a file URI, not a raw C:\\ path.
                iv_open(_engine, new Uri(_path).AbsoluteUri);
                SetState(WallpaperPlayer.PlaybackState.Playing);
                _poll.Start();
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException
                or EntryPointNotFoundException or BadImageFormatException
                or InvalidOperationException)
            {
                Stop();
                ErrorOccurred?.Invoke("Native GPU playback could not start: " + ex.Message +
                    " Switch Video engine to Windows for a compatible fallback.");
                return false;
            }
        }

        private void SendOptions()
        {
            if (_engine != IntPtr.Zero)
                iv_options(_engine, _stretch == Stretch.Uniform ? 0 : 1,
                    _loop ? 1 : 0, _speed, _muted ? 0 : _volume);
        }

        private void PollNative()
        {
            if (_disposed || _engine == IntPtr.Zero)
            {
                _poll.Stop();
                return;
            }
            int status = iv_state(_engine);
            if (status == -1)
            {
                int hr = iv_error(_engine);
                Stop();
                ErrorOccurred?.Invoke(
                    $"Native GPU rendering failed (0x{hr:X8}). " +
                    "This video may need a different Windows codec or Video engine.");
                return;
            }

            if (!_opened && status is 1 or 2 or 3)
            {
                int width = iv_width(_engine);
                int height = iv_height(_engine);
                if (width > 0 && height > 0)
                {
                    _opened = true;
                    NaturalVideoWidth = width;
                    NaturalVideoHeight = height;
                    Duration = TimeSpan.FromMilliseconds(
                        Math.Max(0, iv_duration_ms(_engine)));
                    MediaOpened?.Invoke(Duration);
                }
            }

            if (status == 3 && CurrentState != WallpaperPlayer.PlaybackState.Paused)
                SetState(WallpaperPlayer.PlaybackState.Paused);
            else if (status == 2 && CurrentState != WallpaperPlayer.PlaybackState.Playing)
                SetState(WallpaperPlayer.PlaybackState.Playing);
        }

        public void Pause()
        {
            if (_engine == IntPtr.Zero ||
                CurrentState != WallpaperPlayer.PlaybackState.Playing) return;
            iv_pause(_engine, 1);
            SetState(WallpaperPlayer.PlaybackState.Paused);
        }

        public void PauseAt(TimeSpan position)
        {
            Seek(position);
            Pause();
        }

        public void Resume()
        {
            if (_engine == IntPtr.Zero ||
                CurrentState != WallpaperPlayer.PlaybackState.Paused) return;
            iv_pause(_engine, 0);
            SetState(WallpaperPlayer.PlaybackState.Playing);
        }

        public void Seek(TimeSpan position)
        {
            if (_engine == IntPtr.Zero) return;
            iv_seek(_engine, Math.Max(0, position.TotalSeconds));
        }

        public void SetMute(bool value)
        {
            _muted = value;
            SendOptions();
        }

        public void ToggleMute() => SetMute(!_muted);

        public void Stop()
        {
            _poll.Stop();
            if (_engine != IntPtr.Zero)
            {
                iv_stop(_engine);
                iv_destroy(_engine);
                _engine = IntPtr.Zero;
            }
            _path = null;
            _opened = false;
            NaturalVideoWidth = NaturalVideoHeight = 0;
            Duration = TimeSpan.Zero;
            SetState(WallpaperPlayer.PlaybackState.Stopped);
        }

        private void SetState(WallpaperPlayer.PlaybackState next)
        {
            if (CurrentState == next) return;
            CurrentState = next;
            StateChanged?.Invoke(next);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _host?.Dispose();
            _host = null;
        }
    }
}
