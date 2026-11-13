using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using static Intonaco.Core.NativeMfPlay;

namespace Intonaco.Core
{
    /// <summary>
    /// Experimental in-process Windows video renderer. Media Foundation EVR
    /// presents directly to a child HWND instead of into WPF's MediaElement.
    /// No bundled player, separate process, downloaded codec or 4K bitmap
    /// cache. Keeps a fixed number of OS-managed decoder surfaces.
    ///
    /// This still uses the operating-system decoder; we do not control its
    /// buffer count or promise a particular memory footprint for 4K HEVC.
    /// </summary>
    public sealed class NativeMfPlayWallpaperPlayer : IWallpaperPlayer
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private NativeVideoWindowHost? _host;
        private IMfpMediaPlayer? _player;
        private PlaybackCallback? _callback;
        private string? _sourceFile;
        private bool _disposed;
        private bool _opened;
        private bool _pendingPaused;
        private bool _isMuted = true;
        private bool _loop = true;
        private int _version;
        private int _targetFps = 30;
        private double _volume;
        private double _speed = 1;
        private Stretch _stretch = Stretch.UniformToFill;
        private TimeSpan _pendingSeek;

        public event Action<string>? ErrorOccurred;
        public event Action<WallpaperPlayer.PlaybackState>? StateChanged;
        public event Action<TimeSpan>? MediaOpened;

        public WallpaperPlayer.PlaybackState CurrentState { get; private set; } =
            WallpaperPlayer.PlaybackState.Stopped;
        public bool IsMuted => _isMuted;
        public string? CurrentFilePath => _sourceFile;
        public int NaturalVideoWidth { get; private set; }
        public int NaturalVideoHeight { get; private set; }
        public TimeSpan Duration { get; private set; }

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
                _stretch = value;
                if (_opened)
                    RefreshOutput();
            }
        }

        public double PlaybackSpeed
        {
            get => _speed;
            set
            {
                _speed = Math.Clamp(value, 0.25, 2.0);
                if (_opened)
                    TrySetRate();
            }
        }

        public bool LoopPlayback
        {
            get => _loop;
            set => _loop = value;
        }

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0, 1);
                if (_opened)
                    TryApplyAudio();
            }
        }

        public TimeSpan Position
        {
            get
            {
                if (!_opened || _player == null)
                    return _pendingSeek;

                NativePropVariant variant = default;
                try
                {
                    Guid type = Position100Ns;
                    _player.GetPosition(ref type, out variant);
                    return variant.AsTime();
                }
                catch (COMException)
                {
                    return _pendingSeek;
                }
                finally
                {
                    if (variant.VariantType != 0)
                        PropVariantClear(ref variant);
                }
            }
        }

        public FrameworkElement CreateHost()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_host == null)
            {
                _host = new NativeVideoWindowHost();
                _host.SizeChanged += (_, _) =>
                {
                    if (_opened)
                        RefreshOutput();
                };
            }
            return _host;
        }

        public bool Play(string path)
        {
            if (_disposed || !MediaFormatProbe.TryValidate(path, out string reason))
            {
                ErrorOccurred?.Invoke(_disposed ? "Native video player has been disposed." : reason);
                return false;
            }

            // Only one decoder exists at any time. Shutdown releases the old
            // EVR/Media Foundation pipeline before creating the new source.
            Stop();
            if (_host == null || _host.Handle == IntPtr.Zero)
            {
                ErrorOccurred?.Invoke("The built-in video window has not been initialized.");
                return false;
            }

            _sourceFile = Path.GetFullPath(path);
            _pendingSeek = TimeSpan.Zero;
            _pendingPaused = false;
            int session = ++_version;
            _callback = new PlaybackCallback(this, session);
            try
            {
                // File URLs are required by the Media Foundation source resolver.
                string url = new Uri(_sourceFile).AbsoluteUri;
                CreatePlayer(url, startPlayback: false, 0, _callback,
                    _host.Handle, out IMfpMediaPlayer player);
                _player = player;
                // MFPlay opens asynchronously; MediaItemSet begins playback.
                SetState(WallpaperPlayer.PlaybackState.Playing);
                DebugLog.Write("Native MFPlay created in-process; video file=" +
                    Path.GetFileName(_sourceFile) + "; no external player.");
                return true;
            }
            catch (Exception ex) when (
                ex is COMException or DllNotFoundException or EntryPointNotFoundException
                    or BadImageFormatException or InvalidOperationException)
            {
                DebugLog.Write("Native MFPlay startup failed: " + ex);
                Stop();
                ErrorOccurred?.Invoke(
                    "Built-in Windows renderer could not start: " + ex.Message +
                    ". Select the Windows compatibility backend in Playback settings.");
                return false;
            }
        }

        private void OnNativeEvent(int session, IntPtr eventPointer)
        {
            if (session != _version || _disposed || eventPointer == IntPtr.Zero)
                return;

            EventHeader header;
            try { header = Marshal.PtrToStructure<EventHeader>(eventPointer); }
            catch (Exception ex)
            {
                DebugLog.Write("Native event header unavailable: " + ex.Message);
                return;
            }

            if (header.HResult < 0)
            {
                Post(session, () => ReportError(
                    "Windows native video engine failed (HRESULT 0x" +
                    unchecked((uint)header.HResult).ToString("X8") + ")."));
                return;
            }

            if (header.EventType == MediaItemCreated)
            {
                // This COM interface is valid only for the callback's lifetime.
                // AddRef it before dispatching; Release on every path.
                IntPtr itemPointer = Marshal.ReadIntPtr(
                    eventPointer, Marshal.SizeOf<EventHeader>());
                if (itemPointer == IntPtr.Zero)
                {
                    Post(session, () => ReportError("Windows returned no media item."));
                    return;
                }

                Marshal.AddRef(itemPointer);
                Post(session, () =>
                {
                    try
                    {
                        object itemObject = Marshal.GetObjectForIUnknown(itemPointer);
                        try
                        {
                            if (itemObject is not IMfpMediaItem item)
                                throw new InvalidCastException("Invalid Windows media item.");

                            item.HasVideo(out bool video, out bool selected);
                            if (!video || !selected)
                                throw new NotSupportedException(
                                    "This wallpaper has no playable video stream.");
                            _player?.SetMediaItem(item);
                        }
                        finally
                        {
                            if (Marshal.IsComObject(itemObject))
                                Marshal.ReleaseComObject(itemObject);
                        }
                    }
                    catch (Exception ex)
                    {
                        ReportError("Could not attach native video stream: " + ex.Message);
                    }
                    finally { Marshal.Release(itemPointer); }
                });
                return;
            }

            if (header.EventType == MediaItemSet)
            {
                Post(session, OnMediaItemReady);
                return;
            }

            if (header.EventType == Playing && _pendingPaused)
            {
                Post(session, () =>
                {
                    if (_pendingPaused && _opened)
                    {
                        try { _player?.Pause(); }
                        catch (COMException ex)
                        {
                            DebugLog.Write("Native pending pause failed: " + ex.Message);
                        }
                    }
                });
                return;
            }

            if (header.EventType == PlaybackEnded)
            {
                Post(session, () =>
                {
                    if (!_opened || _player == null)
                        return;

                    if (_loop)
                    {
                        try
                        {
                            Seek(TimeSpan.Zero);
                            _player.Play();
                        }
                        catch (COMException ex) { ReportError(ex.Message); }
                    }
                    else
                    {
                        _pendingPaused = true;
                        SetState(WallpaperPlayer.PlaybackState.Stopped);
                    }
                });
            }
        }

        private void OnMediaItemReady()
        {
            if (_player == null || _disposed)
                return;

            try
            {
                Guid type = Position100Ns;
                NativePropVariant durationValue = default;
                try
                {
                    _player.GetDuration(ref type, out durationValue);
                    Duration = durationValue.AsTime();
                }
                catch (COMException) { Duration = TimeSpan.Zero; }
                finally
                {
                    if (durationValue.VariantType != 0)
                        PropVariantClear(ref durationValue);
                }

                try
                {
                    _player.GetNativeVideoSize(out VideoSize source, out _);
                    NaturalVideoWidth = source.Width;
                    NaturalVideoHeight = source.Height;
                }
                catch (COMException)
                {
                    NaturalVideoWidth = NaturalVideoHeight = 0;
                }

                _opened = true;
                RefreshOutput();
                TryApplyAudio();
                TrySetRate();
                if (_pendingSeek > TimeSpan.Zero)
                    Seek(_pendingSeek);

                _player.Play();
                if (_pendingPaused)
                {
                    // Pause once the first PLAY event confirms the media clock
                    // has started, retaining the real native video surface.
                    SetState(WallpaperPlayer.PlaybackState.Paused);
                }
                else
                {
                    SetState(WallpaperPlayer.PlaybackState.Playing);
                }
                MediaOpened?.Invoke(Duration);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                ReportError("Windows could not begin native playback: " + ex.Message);
            }
        }

        private void Post(int session, Action action)
        {
            if (_disposed || _dispatcher.HasShutdownStarted)
                return;

            try
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background,
                    (Action)(() =>
                    {
                        if (!_disposed && session == _version)
                            action();
                    }));
            }
            catch (InvalidOperationException) { }
        }

        private void RefreshOutput()
        {
            if (_player == null || _host == null)
                return;

            try
            {
                // Always preserve color source and geometric pixel aspect.
                // Fit = whole frame, Fill = crop rather than stretch or blur.
                _player.SetAspectRatioMode(1); // MFVideoARMode_PreservePicture
                VideoRect rect = VideoRect.Full;
                if (_stretch != Stretch.Uniform &&
                    NaturalVideoWidth > 0 && NaturalVideoHeight > 0 &&
                    _host.ActualWidth > 0 && _host.ActualHeight > 0)
                {
                    double sourceAspect = (double)NaturalVideoWidth / NaturalVideoHeight;
                    double destinationAspect = _host.ActualWidth / _host.ActualHeight;
                    if (sourceAspect > destinationAspect)
                    {
                        float crop = (float)((1 - destinationAspect / sourceAspect) / 2);
                        rect.Left = crop;
                        rect.Right = 1 - crop;
                    }
                    else if (sourceAspect < destinationAspect)
                    {
                        float crop = (float)((1 - sourceAspect / destinationAspect) / 2);
                        rect.Top = crop;
                        rect.Bottom = 1 - crop;
                    }
                }

                _player.SetVideoSourceRect(ref rect);
                _player.UpdateVideo();
            }
            catch (COMException ex)
            {
                DebugLog.Write("Native video placement unavailable: " + ex.Message);
            }
        }

        private void TryApplyAudio()
        {
            if (_player == null)
                return;
            try
            {
                _player.SetMute(_isMuted);
                _player.SetVolume((float)_volume);
            }
            catch (COMException ex)
            {
                DebugLog.Write("Native audio state unavailable: " + ex.Message);
            }
        }

        private void TrySetRate()
        {
            if (_player == null)
                return;
            try { _player.SetRate((float)_speed); }
            catch (COMException ex)
            {
                // Media Foundation can reject rates for some codecs.
                DebugLog.Write("Native source rate not supported: " + ex.Message);
            }
        }

        public void Pause()
        {
            if (_disposed || CurrentState != WallpaperPlayer.PlaybackState.Playing)
                return;
            _pendingPaused = true;
            _pendingSeek = Position;
            if (_opened)
            {
                try { _player?.Pause(); }
                catch (COMException ex)
                {
                    DebugLog.Write("Native pause unavailable: " + ex.Message);
                }
            }
            SetState(WallpaperPlayer.PlaybackState.Paused);
        }

        public void PauseAt(TimeSpan position)
        {
            Seek(position);
            if (CurrentState == WallpaperPlayer.PlaybackState.Playing)
                Pause();
        }

        public void Resume()
        {
            if (_disposed || CurrentState != WallpaperPlayer.PlaybackState.Paused)
                return;
            _pendingPaused = false;
            if (_opened)
            {
                try { _player?.Play(); }
                catch (COMException ex) { ReportError(ex.Message); return; }
            }
            SetState(WallpaperPlayer.PlaybackState.Playing);
        }

        public void Seek(TimeSpan position)
        {
            _pendingSeek = TimeSpan.FromTicks(Math.Max(0, position.Ticks));
            if (!_opened || _player == null)
                return;

            NativePropVariant pv = NativePropVariant.FromTime(_pendingSeek);
            Guid type = Position100Ns;
            try { _player.SetPosition(ref type, ref pv); }
            catch (COMException ex) { DebugLog.Write("Native seek unavailable: " + ex.Message); }
        }

        public void SetMute(bool muted)
        {
            _isMuted = muted;
            if (_opened)
                TryApplyAudio();
        }

        public void ToggleMute() => SetMute(!_isMuted);

        public void Stop()
        {
            ++_version;
            _opened = false;
            _pendingPaused = false;
            _pendingSeek = TimeSpan.Zero;

            var player = _player;
            _player = null;
            if (player != null)
            {
                try { player.Stop(); }
                catch (COMException) { }
                try { player.Shutdown(); }
                catch (COMException) { }
                try { Marshal.FinalReleaseComObject(player); }
                catch (InvalidComObjectException) { }
            }

            _callback = null;
            _sourceFile = null;
            NaturalVideoWidth = NaturalVideoHeight = 0;
            Duration = TimeSpan.Zero;
            SetState(WallpaperPlayer.PlaybackState.Stopped);
        }

        private void ReportError(string message)
        {
            DebugLog.Write("Native playback error: " + message);
            SetState(WallpaperPlayer.PlaybackState.Error);
            ErrorOccurred?.Invoke(message);
        }

        private void SetState(WallpaperPlayer.PlaybackState state)
        {
            if (CurrentState == state)
                return;
            CurrentState = state;
            StateChanged?.Invoke(state);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            Stop();
            _disposed = true;
            _host?.Dispose();
            _host = null;
        }

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class PlaybackCallback : IMfpMediaPlayerCallback
        {
            private readonly NativeMfPlayWallpaperPlayer _owner;
            private readonly int _session;
            internal PlaybackCallback(NativeMfPlayWallpaperPlayer owner, int session)
            {
                _owner = owner;
                _session = session;
            }

            public void OnMediaPlayerEvent(IntPtr eventHeader)
            {
                try { _owner.OnNativeEvent(_session, eventHeader); }
                catch (Exception ex)
                {
                    DebugLog.Write("Native media callback failed safely: " + ex.Message);
                }
            }
        }
    }
}
