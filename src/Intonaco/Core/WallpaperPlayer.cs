using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Intonaco.Core
{
    /// <summary>
    /// Manages video wallpaper playback using WPF MediaElement backed by
    /// Windows Media Foundation for native hardware-accelerated decoding.
    ///
    /// Pause stops *animation updates only*: it pauses the media clock and
    /// leaves the source, the decoded frame, the MediaElement and all of its
    /// geometry exactly as they were. WPF MediaElement presents video through
    /// a native Media Foundation surface that is not part of the managed
    /// visual tree, so a RenderTargetBitmap "photograph" of that element is
    /// not a faithful copy of the desktop: it can contain black bands or
    /// blocks, and closing the source destroys the only surface that held
    /// the real frame. Swapping the live surface for a bitmap on pause is
    /// what produced black borders. Resume is therefore just Play() on the
    /// same pipeline - no reopen, no seek, no frame replacement.
    /// </summary>
    public sealed class WallpaperPlayer : IDisposable
    {
        private const int MaxPausedFrameWidth = 3840;
        private const int MaxPausedFrameHeight = 2160;
        private const long MaxPausedFramePixels = 3840L * 2160L;

        private enum PendingMediaAction
        {
            None,
            Resume,
            RefreshPausedFrame,
            RestorePausedPosition
        }

        private MediaElement? _mediaElement;
        private Grid? _host;
        private Image? _pausedImage;
        private BitmapSource? _pausedFrame;
        private Uri? _currentSource;
        private DispatcherTimer? _pausedFrameRefreshTimer;
        private EventHandler? _hidePausedFrameHandler;
        private PendingMediaAction _pendingMediaAction = PendingMediaAction.None;
        private TimeSpan _pendingPosition;
        private TimeSpan _pausedPosition;
        private bool _isMediaOpened;
        private bool _disposed;
        private bool _isPaused;
        private bool _isMuted;
        private double _volume = 0.0; // Default muted for wallpaper
        private int _targetFps = 30;
        private Stretch _stretchMode = Stretch.UniformToFill;
        private double _playbackSpeed = 1.0;
        private bool _loopPlayback = true;

        // Do not subscribe to CompositionTarget.Rendering for FPS statistics:
        // that callback can keep WPF compositing even with the UI hidden.
        // DesiredFrameRate is a hint; the native decoder determines the actual
        // presentation cadence. Profile real CPU/GPU on device instead.

        public event Action<string>? ErrorOccurred;
        public event Action<PlaybackState>? StateChanged;
        public event Action<TimeSpan>? MediaOpened;
        public event Action<BitmapSource?>? PausedFrameChanged;

        public enum PlaybackState
        {
            Stopped,
            Playing,
            Paused,
            Error
        }

        public PlaybackState CurrentState { get; private set; } = PlaybackState.Stopped;
        public bool IsPaused => _isPaused;
        public bool IsMuted => _isMuted;

        /// <summary>
        /// Legacy suspension flag, retained for the dormant static-frame
        /// helpers below. Pause no longer closes the Media Foundation
        /// pipeline, so this is always false in normal operation.
        /// </summary>
        public bool IsSuspended { get; private set; }

        /// <summary>
        /// Legacy frozen-frame bitmap. Always null now: the live MediaElement
        /// keeps presenting the last decoded frame while paused.
        /// </summary>
        public BitmapSource? PausedFrame => _pausedFrame;

        internal bool HasActiveMediaPipeline => _mediaElement?.Source != null;

        public double Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0.0, 1.0);
                if (_mediaElement != null && _pendingMediaAction != PendingMediaAction.RefreshPausedFrame)
                    _mediaElement.Volume = _volume;
            }
        }

        public int TargetFps
        {
            get => _targetFps;
            set
            {
                _targetFps = Math.Clamp(value, 1, 60);

                // A live frame-rate change must reach the media clock
                // immediately, not only on the next ApplyMediaSettings.
                ApplyFrameRateCap();
            }
        }

        public Stretch StretchMode
        {
            get => _stretchMode;
            set
            {
                bool changed = _stretchMode != value;
                _stretchMode = value;
                if (_mediaElement != null)
                    _mediaElement.Stretch = value;

                // The live surface is still the element being rendered while
                // paused, so its Stretch assignment above is all that is
                // needed - no recapture or decoder reopen.
                if (changed && _pausedImage != null && _pausedImage.Visibility == Visibility.Visible)
                    _pausedImage.Stretch = Stretch.UniformToFill;
            }
        }

        public double PlaybackSpeed
        {
            get => _playbackSpeed;
            set
            {
                _playbackSpeed = double.IsNaN(value) || double.IsInfinity(value)
                    ? 1.0
                    : Math.Clamp(value, 0.25, 2.0);
                if (_mediaElement != null)
                    _mediaElement.SpeedRatio = _playbackSpeed;
            }
        }

        public bool LoopPlayback
        {
            get => _loopPlayback;
            set => _loopPlayback = value;
        }

        public TimeSpan Duration { get; private set; }
        public int NaturalVideoWidth { get; private set; }
        public int NaturalVideoHeight { get; private set; }
        public string? CurrentFilePath => _currentSource?.LocalPath;

        public TimeSpan Position
        {
            get
            {
                // While paused, the clock is stopped but the source stays
                // open; report the position captured at pause time so a
                // recovery/reopen cannot jump to a different frame.
                if (IsSuspended ||
                    CurrentState == PlaybackState.Paused ||
                    _pendingMediaAction == PendingMediaAction.RestorePausedPosition)
                    return _pausedPosition;

                try
                {
                    return _mediaElement?.Position ?? _pausedPosition;
                }
                catch
                {
                    return _pausedPosition;
                }
            }
        }

        /// <summary>
        /// Seeks the already-open native surface without replacing the decoder.
        /// While paused, keep the requested frame as the new pause position.
        /// </summary>
        public void Seek(TimeSpan position)
        {
            if (_disposed || _mediaElement?.Source == null || !_isMediaOpened)
                return;

            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;
            if (Duration > TimeSpan.Zero && position > Duration)
                position = Duration;

            try
            {
                _mediaElement.Position = position;
                if (CurrentState == PlaybackState.Paused)
                    _pausedPosition = position;
            }
            catch (Exception ex)
            {
                DebugLog.Write("Seek failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Initializes the player and returns the MediaElement. Desktop code
        /// should prefer <see cref="CreateHost"/>, which also creates the
        /// static paused-frame layer above the video.
        /// </summary>
        public MediaElement Initialize()
        {
            CancelPendingFrameWork();
            ReleaseMediaElement(_mediaElement);
            _mediaElement = null;
            _host = null;
            _pausedImage = null;
            ClearPausedFrame();
            _pendingMediaAction = PendingMediaAction.None;
            _isMediaOpened = false;
            IsSuspended = false;
            _pausedPosition = TimeSpan.Zero;

            _mediaElement = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                ScrubbingEnabled = true,
                Stretch = _stretchMode,
                SpeedRatio = _playbackSpeed,
                Volume = _volume,
                IsMuted = true, // Wallpapers should be silent by default
                IsHitTestVisible = false,
                // The element box itself must span the whole desktop host;
                // Stretch then decides how the video fills that box.
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _mediaElement.MediaOpened += OnMediaOpened;
            _mediaElement.MediaEnded += OnMediaEnded;
            _mediaElement.MediaFailed += OnMediaFailed;

            return _mediaElement;
        }

        /// <summary>
        /// Creates the desktop host. The MediaElement is the single rendering
        /// surface for every state (playing, paused, auto-paused). The
        /// secondary Image is a dormant compatibility layer that is never
        /// shown - no pause path captures or swaps a bitmap any more.
        /// </summary>
        public FrameworkElement CreateHost()
        {
            var mediaElement = Initialize();

            _pausedImage = new Image
            {
                // Cover, never letterbox. The stored frame is cropped to the
                // desktop host's aspect ratio at capture time, so covering it
                // reproduces the live wallpaper pixel-for-pixel — while
                // Stretch.Uniform (or any capture with a mismatched aspect)
                // would put black bars down the sides of a paused wallpaper.
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(
                _pausedImage, BitmapScalingMode.HighQuality);

            _host = new Grid
            {
                Background = Brushes.Black,
                ClipToBounds = true,
                IsHitTestVisible = false,
                // Explicit full-bleed: the host must occupy every pixel of the
                // desktop layer so neither the live video nor the frozen frame
                // can be inset and reveal black at the edges.
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _host.Children.Add(mediaElement);
            _host.Children.Add(_pausedImage);

            // The host root carries the frame-rate cap too, so the value is
            // still in scope if the MediaElement is re-created on resume.
            ApplyFrameRateCap();

            return _host;
        }

        /// <summary>
        /// Loads and begins playing a video file as wallpaper.
        /// </summary>
        public bool Play(string filePath)
        {
            if (_disposed || _mediaElement == null) return false;

            // Reject unreadable/unsupported files before Media Foundation is
            // involved: opening an undecodable container used to leave the
            // desktop black with no explanation.
            if (!MediaFormatProbe.TryValidate(filePath, out string probeError))
            {
                DebugLog.Write($"Play rejected '{filePath}': {probeError}");
                ErrorOccurred?.Invoke(probeError);
                SetState(PlaybackState.Error);
                return false;
            }

            try
            {
                Stop();

                _currentSource = new Uri(filePath, UriKind.Absolute);
                _isMediaOpened = false;
                _pendingMediaAction = PendingMediaAction.None;
                _mediaElement.Visibility = Visibility.Visible;
                _mediaElement.Source = _currentSource;
                ApplyMediaSettings();
                _mediaElement.Play();
                _isPaused = false;
                SetState(PlaybackState.Playing);
                return true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Failed to play: {ex.Message}");
                SetState(PlaybackState.Error);
                return false;
            }
        }

        /// <summary>
        /// Stops animation updates without changing anything on screen.
        ///
        /// The last decoded frame is still presented by the same MediaElement
        /// with the same Stretch, size and position as one tick earlier. The
        /// source is deliberately NOT closed and the element is NOT collapsed:
        /// the WPF MediaElement video surface is a native Media Foundation
        /// target, and tearing it down (or replacing it with a bitmap) is what
        /// exposed black borders/blocks around a paused wallpaper. This is
        /// exactly the v1 pause behaviour, restored while keeping all later
        /// features (fps cap, preview, auto-pause, fit/speed settings).
        /// </summary>
        public void Pause()
        {
            if (_disposed || _mediaElement == null || CurrentState != PlaybackState.Playing) return;

            try
            {
                CancelPendingFrameWork();
                _pendingMediaAction = PendingMediaAction.None;

                // One operation stops all animation updates: the media clock.
                _mediaElement.Pause();
                _pausedPosition = SafeCurrentPosition();
                _isPaused = true;

                // The rendering surface is the MediaElement itself. Nothing is
                // hidden, cleared, collapsed or overlaid, so the background
                // stays fully rendered while animation updates are stopped.
                IsSuspended = false;
                _mediaElement.Visibility = Visibility.Visible;
                ClearPausedFrame();

                SetState(PlaybackState.Paused);
                DebugLog.Write(
                    $"Wallpaper paused in place at {_pausedPosition}; " +
                    "media surface kept (no frame capture, no source close).");
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Pause failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Restores a paused wallpaper after Explorer-restart recovery at the
        /// position it was paused at. The desktop still renders through the
        /// live MediaElement: no frozen bitmap is carried over from the
        /// previous (now destroyed) player window.
        /// </summary>
        internal void PauseAt(TimeSpan position)
        {
            if (_disposed || _mediaElement == null ||
                (CurrentState != PlaybackState.Playing && CurrentState != PlaybackState.Paused))
            {
                return;
            }

            try
            {
                CancelPendingFrameWork();
                _pendingMediaAction = PendingMediaAction.None;
                _mediaElement.Pause();
                _pausedPosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
                _isPaused = true;
                IsSuspended = false;
                _mediaElement.Visibility = Visibility.Visible;
                ClearPausedFrame();

                if (_isMediaOpened)
                {
                    ApplyPausedPosition();
                }
                else
                {
                    // The source has not opened yet: Pause() before MediaOpened
                    // is not always honoured, so MediaOpened must repeat the
                    // pause (and the seek when there is a saved position).
                    _pendingPosition = _pausedPosition;
                    _pendingMediaAction = PendingMediaAction.RestorePausedPosition;
                }

                SetState(PlaybackState.Paused);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Pause failed: {ex.Message}");
            }
        }

        /// <summary>Seeks a paused (but open) source to its frozen position.</summary>
        private void ApplyPausedPosition()
        {
            if (_mediaElement == null || _pausedPosition <= TimeSpan.Zero)
                return;

            if (Duration > TimeSpan.Zero && _pausedPosition > Duration)
                _pausedPosition = Duration;

            try
            {
                // ScrubbingEnabled is set on the element, so WPF asks Media
                // Foundation to present the frame at this position while the
                // clock remains paused.
                _mediaElement.Position = _pausedPosition;
            }
            catch
            {
                // Some containers cannot seek before a keyframe; staying at
                // the current frame is still visually correct.
            }
        }

        /// <summary>
        /// Restarts animation updates on the very same rendering surface.
        /// Because Pause never closed the source, there is no reopen, no seek,
        /// no decoder warm-up and no frame swap - resume is a single Play()
        /// on the clock that was paused, so there is no flicker or jump.
        /// </summary>
        public void Resume()
        {
            if (_disposed || _mediaElement == null || CurrentState != PlaybackState.Paused) return;

            try
            {
                CancelHidePausedFrame();
                _pausedFrameRefreshTimer?.Stop();
                _pendingMediaAction = PendingMediaAction.None;
                IsSuspended = false;
                _mediaElement.Visibility = Visibility.Visible;

                // Non-looping clips that ended are restarted from the start;
                // every other resume continues at the exact paused frame.
                if (Duration > TimeSpan.Zero &&
                    SafeCurrentPosition() >= Duration - TimeSpan.FromMilliseconds(250))
                {
                    _mediaElement.Position = TimeSpan.Zero;
                }

                ApplyMediaSettings();
                _mediaElement.Play();
                _isPaused = false;
                ClearPausedFrame();
                SetState(PlaybackState.Playing);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Resume failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Recaptures the suspended frame after a layout-affecting change
        /// (fit mode or display size), then closes the decoder again. The
        /// old static frame stays visible throughout.
        /// </summary>
        public void RefreshPausedFrame()
        {
            // Legacy static-frame path. No pause path suspends the pipeline
            // any more, so this is unreachable in normal operation; it stays
            // defensive for any external caller of an older API.
            if (_disposed || _mediaElement == null || _currentSource == null ||
                CurrentState != PlaybackState.Paused || !IsSuspended ||
                _pendingMediaAction != PendingMediaAction.None)
            {
                return;
            }

            try
            {
                _pendingPosition = _pausedPosition;
                _pendingMediaAction = PendingMediaAction.RefreshPausedFrame;
                IsSuspended = false;
                _isMediaOpened = false;
                _mediaElement.Visibility = Visibility.Visible;
                _mediaElement.Source = _currentSource;
                ApplyMediaSettings();

                // Never make a silent recapture audible.
                _mediaElement.IsMuted = true;
                _mediaElement.Volume = 0;
            }
            catch (Exception ex)
            {
                _pendingMediaAction = PendingMediaAction.None;
                IsSuspended = true;
                DebugLog.Write($"Paused-frame refresh failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops playback and releases the media source and paused frame.
        /// </summary>
        public void Stop()
        {
            CancelPendingFrameWork();
            _pendingMediaAction = PendingMediaAction.None;

            if (_mediaElement != null)
            {
                ReleaseMediaPipeline();
                _mediaElement.Visibility = Visibility.Visible;
            }

            _currentSource = null;
            _isPaused = false;
            IsSuspended = false;
            _isMediaOpened = false;
            _pausedPosition = TimeSpan.Zero;
            _pendingMediaAction = PendingMediaAction.None;
            _pendingPosition = TimeSpan.Zero;
            ClearPausedFrame();
            Duration = TimeSpan.Zero;
            NaturalVideoWidth = 0;
            NaturalVideoHeight = 0;
            SetState(PlaybackState.Stopped);
        }

        public void ToggleMute()
        {
            _isMuted = !_isMuted;
            if (_mediaElement != null && _pendingMediaAction != PendingMediaAction.RefreshPausedFrame)
                _mediaElement.IsMuted = _isMuted;
        }

        public void SetMute(bool muted)
        {
            _isMuted = muted;
            if (_mediaElement != null && _pendingMediaAction != PendingMediaAction.RefreshPausedFrame)
                _mediaElement.IsMuted = _isMuted;
        }

        internal static double CalculatePausedFrameScale(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return 1.0;

            double scale = Math.Min(
                (double)MaxPausedFrameWidth / width,
                (double)MaxPausedFrameHeight / height);
            long pixels = (long)width * height;
            if (pixels > MaxPausedFramePixels)
                scale = Math.Min(scale, Math.Sqrt((double)MaxPausedFramePixels / pixels));

            return Math.Min(1.0, scale);
        }

        /// <summary>
        /// Centre-crops <paramref name="source"/> so its aspect ratio matches
        /// <paramref name="targetAspect"/> (width / height). A paused frame
        /// whose aspect disagrees with the desktop is what produced black bars
        /// down the sides: the letterbox was baked into the captured pixels and
        /// then stretched to full screen. Cropping makes that structurally
        /// impossible — the stored frame always has the desktop's shape.
        /// Returns the source unchanged when it already matches.
        /// </summary>
        internal static BitmapSource CentreCropToAspect(
            BitmapSource source, double targetAspect, double tolerance = 0.01)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (double.IsNaN(targetAspect) || targetAspect <= 0 ||
                source.PixelWidth < 1 || source.PixelHeight < 1)
            {
                return source;
            }

            double current = (double)source.PixelWidth / source.PixelHeight;
            if (Math.Abs(current - targetAspect) <= targetAspect * tolerance)
                return source;

            int cropWidth;
            int cropHeight;

            if (current > targetAspect)
            {
                // Wider than the desktop: trim the sides.
                cropHeight = source.PixelHeight;
                cropWidth = (int)Math.Round(source.PixelHeight * targetAspect);
            }
            else
            {
                // Taller than the desktop: trim the top and bottom.
                cropWidth = source.PixelWidth;
                cropHeight = (int)Math.Round(source.PixelWidth / targetAspect);
            }

            cropWidth = Math.Clamp(cropWidth, 1, source.PixelWidth);
            cropHeight = Math.Clamp(cropHeight, 1, source.PixelHeight);

            var cropped = new CroppedBitmap(
                source,
                new Int32Rect(
                    (source.PixelWidth - cropWidth) / 2,
                    (source.PixelHeight - cropHeight) / 2,
                    cropWidth,
                    cropHeight));

            if (cropped.CanFreeze)
                cropped.Freeze();

            return cropped;
        }

        /// <summary>
        /// Captures the frame currently presented by <paramref name="mediaElement"/>.
        /// When <paramref name="targetAspect"/> is supplied the result is cropped
        /// to it, so the returned bitmap always matches the desktop's shape.
        /// </summary>
        internal static BitmapSource? CaptureFrame(
            MediaElement mediaElement, double targetAspect = double.NaN)
        {
            try
            {
                if (mediaElement.Source == null)
                    return null;

                mediaElement.UpdateLayout();
                int width = (int)Math.Ceiling(mediaElement.ActualWidth);
                int height = (int)Math.Ceiling(mediaElement.ActualHeight);
                if (width < 2 || height < 2)
                    return null;

                var bitmap = new RenderTargetBitmap(
                    width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(mediaElement);

                BitmapSource result = bitmap;
                double scale = CalculatePausedFrameScale(width, height);
                if (scale < 0.999)
                {
                    var transformed = new TransformedBitmap(
                        bitmap, new ScaleTransform(scale, scale));

                    // Materialize the downscaled pixels. Keeping only the
                    // TransformedBitmap would retain the full-size source.
                    result = new WriteableBitmap(transformed);
                }

                // Uniform scaling preserves the aspect ratio, so crop after
                // the downscale and the stored frame stays inside the memory
                // budget while matching the desktop exactly.
                result = CentreCropToAspect(result, targetAspect);

                if (result.CanFreeze)
                    result.Freeze();

                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The desktop host's aspect ratio, or NaN when it has not been laid
        /// out yet. The host — not the media element — is the authoritative
        /// desktop box, so it is what the paused frame must match.
        /// </summary>
        private double HostAspectRatio()
        {
            if (_host == null)
                return double.NaN;

            double width = _host.ActualWidth;
            double height = _host.ActualHeight;
            if (width < 2 || height < 2)
                return double.NaN;

            return width / height;
        }

        private BitmapSource? CaptureCurrentFrame()
        {
            if (_mediaElement == null || !_isMediaOpened ||
                NaturalVideoWidth <= 0 || NaturalVideoHeight <= 0)
            {
                return null;
            }

            // Converge layout first. Suspending from a focus change, a
            // fullscreen transition or a display resize can otherwise capture
            // the media element at a stale size, and the mismatch is what shows
            // up as black bars on the frozen frame.
            _host?.UpdateLayout();
            _mediaElement.UpdateLayout();

            double hostAspect = HostAspectRatio();

            // The frozen frame must cover the desktop whenever the user asked
            // for cover. Photographing the MediaElement as-is bakes whatever
            // letterbox it is showing into the bitmap's pixels, and no container
            // geometry can remove bars that are part of the image — so force
            // cover for the duration of the capture, then restore the user's fit
            // for live playback.
            //
            // "Fit frame" (Stretch.Uniform) is the one mode where the letterbox
            // is deliberate: forcing cover there would make the paused wallpaper
            // zoom and crop relative to the playing one, so it is preserved and
            // stays consistent across the pause.
            Stretch captureStretch = _mediaElement.Stretch;
            bool preserveLetterbox = captureStretch == Stretch.Uniform;
            BitmapSource? frame;
            try
            {
                if (!preserveLetterbox)
                    _mediaElement.Stretch = Stretch.UniformToFill;

                _mediaElement.UpdateLayout();
                frame = CaptureFrame(_mediaElement, hostAspect);
            }
            finally
            {
                _mediaElement.Stretch = captureStretch;
            }

            DebugLog.Write(
                frame == null
                    ? $"Paused frame capture returned nothing (host " +
                      $"{_host?.ActualWidth:0}x{_host?.ActualHeight:0}, media " +
                      $"{_mediaElement.ActualWidth:0}x{_mediaElement.ActualHeight:0}, " +
                      $"video {NaturalVideoWidth}x{NaturalVideoHeight}, fit {captureStretch})."
                    : $"Paused frame captured {frame.PixelWidth}x{frame.PixelHeight} " +
                      $"(host {_host?.ActualWidth:0}x{_host?.ActualHeight:0}, media " +
                      $"{_mediaElement.ActualWidth:0}x{_mediaElement.ActualHeight:0}, " +
                      $"video {NaturalVideoWidth}x{NaturalVideoHeight}, fit {captureStretch}, " +
                      $"captured-as {(preserveLetterbox ? "fit-frame" : "cover")}, " +
                      $"host aspect " +
                      $"{(double.IsNaN(hostAspect) ? "unknown" : hostAspect.ToString("0.###"))}).");

            return frame;
        }

        private void OnMediaOpened(object? sender, RoutedEventArgs e)
        {
            if (_mediaElement == null) return;

            _isMediaOpened = true;
            Duration = _mediaElement.NaturalDuration.HasTimeSpan
                ? _mediaElement.NaturalDuration.TimeSpan
                : TimeSpan.Zero;
            NaturalVideoWidth = _mediaElement.NaturalVideoWidth;
            NaturalVideoHeight = _mediaElement.NaturalVideoHeight;

            PendingMediaAction action = _pendingMediaAction;
            _pendingMediaAction = PendingMediaAction.None;

            if (action != PendingMediaAction.None)
            {
                try
                {
                    TimeSpan target = _pendingPosition;
                    if (Duration > TimeSpan.Zero)
                    {
                        if (target >= Duration - TimeSpan.FromMilliseconds(250))
                            target = TimeSpan.Zero;
                        else if (target > Duration)
                            target = Duration;
                    }

                    if (target > TimeSpan.Zero)
                        _mediaElement.Position = target;
                    else if (action == PendingMediaAction.Resume && _pausedPosition > TimeSpan.Zero)
                        _mediaElement.Position = TimeSpan.Zero;

                    ApplyMediaSettings();

                    if (action == PendingMediaAction.RestorePausedPosition)
                    {
                        // Explorer-restart recovery: seek on the live surface
                        // and leave the clock paused. Never start playback.
                        _mediaElement.Pause();
                        ApplyPausedPosition();
                        ApplyMediaSettings();
                    }
                    else if (action == PendingMediaAction.Resume)
                    {
                        _mediaElement.Play();
                        HidePausedFrameAfterRender();
                    }
                    else
                    {
                        _mediaElement.IsMuted = true;
                        _mediaElement.Volume = 0;
                        _mediaElement.Play();
                        StartPausedFrameRefreshTimer();

                        // OnMediaOpened cleared the field above so user
                        // actions can safely supersede the recapture; mark
                        // the short timer phase as pending again.
                        _pendingMediaAction = PendingMediaAction.RefreshPausedFrame;
                    }
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke($"Failed to restore playback position: {ex.Message}");
                }
            }

            MediaOpened?.Invoke(Duration);
        }

        private void OnMediaEnded(object? sender, RoutedEventArgs e)
        {
            if (_mediaElement == null || _disposed || _isPaused)
                return;

            if (_loopPlayback)
            {
                _mediaElement.Position = TimeSpan.Zero;
                _mediaElement.SpeedRatio = _playbackSpeed;
                _mediaElement.Play();
                return;
            }

            // Capture and suspend the final frame rather than retaining an
            // ended decoder indefinitely; Resume rewinds from the end.
            Pause();
        }

        private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            string raw = e.ErrorException?.Message ?? "Unknown playback error";
            string msg = BuildUserFriendlyError(raw);

            if (_pendingMediaAction == PendingMediaAction.RefreshPausedFrame)
            {
                // The previous static frame is still valid. Release the
                // failed recapture attempt without surfacing a new error.
                _pausedFrameRefreshTimer?.Stop();
                _pendingMediaAction = PendingMediaAction.None;
                ReleaseMediaPipeline();
                IsSuspended = true;
                _isMediaOpened = false;
                if (_mediaElement != null)
                    _mediaElement.Visibility = Visibility.Collapsed;
                DebugLog.Write($"Paused-frame refresh media failed: {raw}");
                return;
            }

            if (_pendingMediaAction == PendingMediaAction.Resume)
            {
                _pendingMediaAction = PendingMediaAction.None;
                ReleaseMediaPipeline();
                IsSuspended = true;
                _isPaused = true;
                _isMediaOpened = false;
                if (_mediaElement != null)
                    _mediaElement.Visibility = Visibility.Collapsed;
            }

            DebugLog.Write($"Media failed: {raw} -> {msg}");
            ErrorOccurred?.Invoke(msg);
            SetState(PlaybackState.Error);
        }

        private static string BuildUserFriendlyError(string rawMessage)
        {
            // Media Foundation errors often contain HRESULTs; map to a friendly
            // codec hint so users aren't left staring at a black screen.
            string lower = rawMessage.ToLowerInvariant();
            bool isCodecError = lower.Contains("0xc00d36b4") // MF_E_INVALIDMEDIATYPE
                             || lower.Contains("0xc00d36c4")
                             || lower.Contains("0xc00d5212")
                             || lower.Contains("unsupported")
                             || lower.Contains("could not be decoded")
                             || lower.Contains("mf_media_engine_err_src_not_supported")
                             || lower.Contains("failed to decode");

            if (isCodecError)
            {
                return "Unable to play video: Unsupported codec or format. Please use standard MP4 (H.264) or WebM files.";
            }

            // Wrap generic failures with the same friendly prefix so toast is actionable.
            if (lower.Contains("not found") || lower.Contains("cannot find"))
                return rawMessage;

            return rawMessage.Contains("Unsupported", StringComparison.OrdinalIgnoreCase)
                ? rawMessage
                : $"Unable to play video: {rawMessage} Try converting to MP4 (H.264).";
        }

        private void StartPausedFrameRefreshTimer()
        {
            _pausedFrameRefreshTimer ??= new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _pausedFrameRefreshTimer.Tick -= OnPausedFrameRefreshTimerTick;
            _pausedFrameRefreshTimer.Tick += OnPausedFrameRefreshTimerTick;
            _pausedFrameRefreshTimer.Stop();
            _pausedFrameRefreshTimer.Start();
        }

        private void OnPausedFrameRefreshTimerTick(object? sender, EventArgs e)
        {
            _pausedFrameRefreshTimer?.Stop();

            if (_disposed || _mediaElement == null ||
                CurrentState != PlaybackState.Paused ||
                _pendingMediaAction != PendingMediaAction.RefreshPausedFrame)
            {
                return;
            }

            _pendingMediaAction = PendingMediaAction.None;

            try
            {
                _mediaElement.Pause();
                _pausedPosition = SafeCurrentPosition();
                BitmapSource? frame = CaptureCurrentFrame();
                if (frame != null)
                    SetPausedFrame(frame);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Paused-frame recapture failed: {ex.Message}");
            }
            finally
            {
                SuspendMediaPipeline();
                ApplyMediaSettings();
            }
        }

        private void HidePausedFrameAfterRender()
        {
            CancelHidePausedFrame();
            int frames = 0;

            _hidePausedFrameHandler = (_, _) =>
            {
                frames++;
                if (frames < 3)
                    return;

                CancelHidePausedFrame();
                if (!_disposed && CurrentState == PlaybackState.Playing)
                    ClearPausedFrame();
            };

            CompositionTarget.Rendering += _hidePausedFrameHandler;
        }

        private void CancelHidePausedFrame()
        {
            if (_hidePausedFrameHandler == null)
                return;

            CompositionTarget.Rendering -= _hidePausedFrameHandler;
            _hidePausedFrameHandler = null;
        }

        private void CancelPendingFrameWork()
        {
            _pausedFrameRefreshTimer?.Stop();
            CancelHidePausedFrame();
        }

        // The methods below are the legacy static-frame subsystem. They are
        // deliberately NOT called by Pause, Resume, PauseAt or any focus path:
        // replacing the native MediaElement surface with a bitmap is the
        // regression that caused black borders. They remain only so the
        // geometry helpers stay available to tests and diagnostics.
        private void SetPausedFrame(BitmapSource frame)
        {
            // Legacy helper: crop any captured frame to the host aspect.
            double hostAspect = HostAspectRatio();
            BitmapSource shown = CentreCropToAspect(frame, hostAspect);

            if (!ReferenceEquals(shown, frame))
            {
                DebugLog.Write(
                    $"Paused frame re-cropped {frame.PixelWidth}x{frame.PixelHeight} -> " +
                    $"{shown.PixelWidth}x{shown.PixelHeight} to match host aspect " +
                    $"{hostAspect:0.###}.");
            }

            _pausedFrame = shown;
            if (_pausedImage != null)
            {
                // Re-assert full-bleed cover every time a frame is shown. The
                // frame is cropped to the desktop aspect, so covering it cannot
                // letterbox; anything else here is what put bars down the sides
                // of a paused wallpaper.
                _pausedImage.Stretch = Stretch.UniformToFill;
                _pausedImage.Margin = new Thickness(0);
                _pausedImage.HorizontalAlignment = HorizontalAlignment.Stretch;
                _pausedImage.VerticalAlignment = VerticalAlignment.Stretch;
                _pausedImage.Source = shown;
                _pausedImage.Visibility = Visibility.Visible;
            }
            PausedFrameChanged?.Invoke(shown);
        }

        private void ClearPausedFrame()
        {
            bool hadFrame = _pausedFrame != null;
            _pausedFrame = null;

            if (_pausedImage != null)
            {
                _pausedImage.Source = null;
                _pausedImage.Visibility = Visibility.Collapsed;
            }

            if (_mediaElement != null && !IsSuspended)
                _mediaElement.Visibility = Visibility.Visible;

            if (hadFrame)
                PausedFrameChanged?.Invoke(null);
        }

        private void SuspendMediaPipeline()
        {
            ReleaseMediaPipeline();
            IsSuspended = true;
            _isMediaOpened = false;
            _pendingMediaAction = PendingMediaAction.None;

            if (_mediaElement != null)
                _mediaElement.Visibility = Visibility.Collapsed;
        }

        private void ReleaseMediaPipeline()
        {
            if (_mediaElement == null)
                return;

            try
            {
                _mediaElement.Stop();
                _mediaElement.Source = null;
                _mediaElement.Close();
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        private void ReleaseMediaElement(MediaElement? element)
        {
            if (element == null)
                return;

            element.MediaOpened -= OnMediaOpened;
            element.MediaEnded -= OnMediaEnded;
            element.MediaFailed -= OnMediaFailed;

            try
            {
                element.Stop();
                element.Source = null;
                element.Close();
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        private void ApplyMediaSettings()
        {
            if (_mediaElement == null)
                return;

            _mediaElement.Stretch = _stretchMode;
            _mediaElement.SpeedRatio = _playbackSpeed;
            _mediaElement.IsMuted = _isMuted;
            _mediaElement.Volume = _volume;

            ApplyFrameRateCap();
        }

        /// <summary>
        /// Caps the media timeline's presentation rate. 60 fps clears the cap
        /// (WPF default), any lower value sets Timeline.DesiredFrameRate, which
        /// is the frame-interval hint the WPF media clock honours: 30 fps means
        /// a ~33.3 ms interval instead of ~16.6 ms, halving the number of
        /// video frames composited onto the desktop.
        ///
        /// The value is written as a local value on the MediaElement only. The
        /// property is an inheriting attached property, so writing it on the
        /// host root as well leaves a stale inherited value behind after a
        /// ClearValue and can re-cap a later 60 fps session.
        /// </summary>
        private void ApplyFrameRateCap()
        {
            if (_mediaElement == null)
                return;

            int fps = Math.Clamp(_targetFps, 1, 60);

            try
            {
                if (fps >= 60)
                    _mediaElement.ClearValue(Timeline.DesiredFrameRateProperty);
                else
                    _mediaElement.SetValue(Timeline.DesiredFrameRateProperty, fps);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Frame-rate cap could not be applied: {ex.Message}");
            }
        }

        /// <summary>
        /// No periodic rendering probe is attached. Continuous measurement
        /// was waking the compositor but never blocked any actual frames.
        /// Use the external performance sampler for trustworthy metrics.
        /// </summary>
        public double MeasuredFps => 0.0;

        private TimeSpan SafeCurrentPosition()
        {
            try
            {
                return _mediaElement?.Position ?? TimeSpan.Zero;
            }
            catch
            {
                return _pausedPosition;
            }
        }

        private void SetState(PlaybackState state)
        {
            if (CurrentState != state)
            {
                CurrentState = state;
                StateChanged?.Invoke(state);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;


            Stop();
            ReleaseMediaElement(_mediaElement);
            _mediaElement = null;
            _pausedImage = null;
            _host = null;
        }
    }
}
