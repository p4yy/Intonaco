using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Intonaco.Core;
using Microsoft.Win32;

namespace Intonaco.Views
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private IWallpaperPlayer _player;
        private readonly DesktopIntegration _desktop;
        private readonly FullscreenDetector _fullscreenDetector;
        private readonly ForegroundDetector _foregroundDetector;
        private readonly SystemEventMonitor _systemEvents;
        private PlayerWindow? _playerWindow;
        private readonly ObservableCollection<RecentWallpaperItem> _recentWallpapers =
            new ObservableCollection<RecentWallpaperItem>();
        private string _fileInfoBase = string.Empty;
        private string? _previewFilePath;
        private bool _livePreviewEnabled;
        private int _previewThumbnailVersion;
        private string? _thumbnailRequestedFor;
        private BitmapSource? _thumbnailImage;
        private string? _activeOriginalFile;
        private int _sourceWidth;
        private int _sourceHeight;
        private int _wallpaperRequestVersion;
        private CancellationTokenSource? _preparationCancellation;
        private string? _rollbackOriginalPath;
        private bool _rollbackWasPaused;
        private TimeSpan _rollbackPosition;
        private bool _lastVideoOpened;
        private bool _previewNeedsPositionSync;

        /// <summary>
        /// Extensions offered by the file dialog. Validation itself is done by
        /// <see cref="MediaFormatProbe"/> so the dialog, drag-and-drop and the
        /// player can never disagree about what is supported.
        /// </summary>
        private static string VideoDialogFilter => MediaFormatProbe.FileDialogFilter;

        private sealed class RecentWallpaperItem
        {
            public required string FullPath { get; init; }
            public required string FileName { get; init; }
            public required string Meta { get; init; }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeLegacy = 19;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;

        private bool _isApplied;
        private bool _pausedByUser;
        private bool _pausedByFullscreen;
        private bool _pausedByForeground;
        private bool _pausedBySystem;
        private bool _suppressEvents = true;
        private bool _isExiting;
        private bool _startMinimized;
        private bool _firstContentRendered;
        private bool _pendingWindowRestore;
        private bool _startupWallpaperScheduled;
        private string? _startupRestoreFile;
        private int _startupAttachRetryCount;

        // Explorer restart detection
        private uint _shellRestartMessage;

        // Periodic desktop-placement self-check (Explorer can reshuffle
        // its desktop windows and cover the player, e.g. after a
        // wallpaper change recreates the system WorkerW). It is started
        // only while a wallpaper is applied and runs less often while the
        // wallpaper is a static suspended frame.
        private DispatcherTimer? _placementTimer;
        private DispatcherTimer? _settingsSaveTimer;
        private DispatcherTimer? _seekUpdateTimer;
        private DispatcherTimer? _seekCommitTimer;
        private TimeSpan _pendingSeekPosition;
        private bool _updatingSeekSlider;

        public MainWindow()
        {
            _settings = AppSettings.Load();
            MpvWallpaperPlayer.ConfiguredExecutablePath = _settings.MpvExecutablePath;
            if (_settings.PlaybackBackend == "MPV" &&
                !MpvWallpaperPlayer.TryLocateExecutable(out _))
            {
                DebugLog.Write("Saved MPV backend unavailable; using the built-in native player.");
                _settings.PlaybackBackend = "Native";
                _settings.Save();
            }
            _player = CreatePlayer(_settings.PlaybackBackend);
            _desktop = new DesktopIntegration();
            _fullscreenDetector = new FullscreenDetector();
            _foregroundDetector = new ForegroundDetector();
            _systemEvents = new SystemEventMonitor();

            InitializeComponent();
            // The default window is intentionally narrow and tall, as shown in
            // the Store screenshots. Keep it inside the usable screen area on
            // laptops and displays with scaling or a larger taskbar.
            Height = CalculateInitialWindowHeight(SystemParameters.WorkArea.Height, MinHeight);
            RecentList.ItemsSource = _recentWallpapers;

            // Footer version comes from the assembly, so it can never drift
            // from the released build number.
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = string.Empty; // Version stays in file metadata for servicing.

            LoadSettings();
            WireEvents();
            InitializeSystemTray();
            UpdateBackendInterface();
            MpvPathStatusText.Text = MpvWallpaperPlayer.TryLocateExecutable(out _)
                ? "MPV available — select it for calibrated video color."
                : "Optional: locate mpv.exe to enable color-managed playback.";

            // A manual launch must always open the controls. Automatic startup
            // with --minimized initializes the WPF surface before hiding it;
            // hiding at SourceInitialized left an unpainted black client area
            // after a later desktop-shortcut launch.
            _startMinimized = StartupManager.ShouldStartMinimized();
#if INTONACO_STORE
            _startMinimized |= StoreStartupManager.IsStartupActivation() &&
                               _settings.StartMinimized;
#endif
            if (_startMinimized)
            {
                ShowActivated = false;
                ShowInTaskbar = false;
                // Keep the first HWND invisible until WPF has completed its
                // first render; no black title-bar window during sign-in.
                Opacity = 0;
                DebugLog.Write("Automatic minimized launch: awaiting first WPF render.");
            }

            // Never start desktop WorkerW probing or video decoding at Loaded
            // priority. First present the controls, then restore playback.
            // Explorer and display drivers can be busy during Windows sign-in.
            ContentRendered += OnInitialContentRendered;
        }

        // WPF dimensions are device-independent units. Clamp only the startup
        // height; resizing/maximizing remains fully supported afterward.
        internal static double CalculateInitialWindowHeight(
            double workAreaHeight, double minHeight) =>
            Math.Max(minHeight, Math.Min(1080, workAreaHeight - 24));

        private static IWallpaperPlayer CreatePlayer(string backend) =>
            AppSettings.NormalizeBackend(backend) switch
            {
                "GPU" => new GpuVideoWallpaperPlayer(),
                "MPV" => new MpvWallpaperPlayer(),
                "Windows" => new WallpaperPlayer(),
                _ => new NativeMfPlayWallpaperPlayer()
            };

        private void ApplyPlayerSettings()
        {
            _player.TargetFps = _settings.TargetFps;
            _player.StretchMode = ParseFitMode(_settings.WallpaperFit);
            _player.PlaybackSpeed = _settings.PlaybackSpeed;
            _player.LoopPlayback = _settings.LoopPlayback;
            _player.Volume = _settings.Volume;
            _player.SetMute(_settings.IsMuted);
        }

        private void WirePlayerEvents()
        {
            _player.StateChanged += OnPlayerStateChanged;
            _player.ErrorOccurred += OnPlayerError;
            _player.MediaOpened += OnMediaOpened;
        }

        private void UnwirePlayerEvents()
        {
            _player.StateChanged -= OnPlayerStateChanged;
            _player.ErrorOccurred -= OnPlayerError;
            _player.MediaOpened -= OnMediaOpened;
        }

        private void UpdateBackendInterface()
        {
            bool mpv = _player is MpvWallpaperPlayer;
            // Each backend follows the native video's source cadence.
            // WPF MediaElement and MPV currently follow the source video
            // frame rate. Timeline.DesiredFrameRate is an animation property,
            // not a MediaElement frame limit. Never advertise a fake FPS cap.
            FpsComboBox.IsEnabled = false;
            FpsComboBox.ToolTip =
                "Video plays at its original frame rate. Changing an FPS limit is unavailable in this renderer.";
            PreviewHintText.Text = _player is GpuVideoWallpaperPlayer
                ? "Intonaco GPU — hardware decoding and direct DXGI presentation."
                : _player is NativeMfPlayWallpaperPlayer
                    ? "Built-in native playback — no external player or extra preview decoder."
                : mpv
                    ? "Optional MPV renders without a second in-app decoder."
                    : "Drop a video here or choose a file";
        }

        private void LoadSettings()
        {
            _suppressEvents = true;
            SelectComboByTag(BackendComboBox, _settings.PlaybackBackend);

            // Dedicated focus-animation mode. Mode A pauses animation
            // updates on focus loss; Mode B keeps them running. The choice is
            // persisted in settings.json and restored on every launch.
            SelectComboByTag(BackgroundAnimationComboBox, _settings.BackgroundAnimationMode);

#if INTONACO_STORE
            StartWithWindowsToggle.IsChecked = _settings.StartWithWindows;
#else
            StartWithWindowsToggle.IsChecked = StartupManager.IsRegistered;
#endif
            StartMinimizedToggle.IsChecked = _settings.StartMinimized;
            AutoPlayToggle.IsChecked = _settings.AutoPlay;
            LoopToggle.IsChecked = _settings.LoopPlayback;
            VolumeSlider.Value = _settings.Volume * 100;
            UpdateVolumeLabel();

            SelectComboByTag(FitComboBox, _settings.WallpaperFit);
            SelectSpeedCombo(_settings.PlaybackSpeed);

            // Set FPS combo
            foreach (ComboBoxItem item in FpsComboBox.Items)
            {
                if (item.Tag is string tag && int.TryParse(tag, out int fps) && fps == _settings.TargetFps)
                {
                    FpsComboBox.SelectedItem = item;
                    break;
                }
            }

            // Set theme combo
            foreach (ComboBoxItem item in ThemeComboBox.Items)
            {
                if (item.Tag is string theme && theme == _settings.Theme)
                {
                    ThemeComboBox.SelectedItem = item;
                    break;
                }
            }

            // Push persisted values into the player (fields only at this point;
            // the MediaElement is created on first ApplyWallpaper).
            _player.TargetFps = _settings.TargetFps;
            _player.StretchMode = ParseFitMode(_settings.WallpaperFit);
            _player.PlaybackSpeed = _settings.PlaybackSpeed;
            _player.LoopPlayback = _settings.LoopPlayback;
            _player.Volume = _settings.Volume;
            _player.SetMute(_settings.IsMuted);
            PreviewMedia.Stretch = _player.StretchMode;
            PreviewMedia.SpeedRatio = _player.PlaybackSpeed;
            UpdateMuteButton();
            RefreshRecentWallpapers();

            if (!string.IsNullOrEmpty(_settings.LastWallpaperPath) &&
                File.Exists(_settings.LastWallpaperPath))
            {
                UpdateFileInfo(_settings.LastWallpaperPath);
                PlayPauseButton.IsEnabled = true;
            }
            UpdateDesktopStatus(_player.CurrentState);

            _suppressEvents = false;
        }

        private void WireEvents()
        {
            WirePlayerEvents();

            // Focus-driven auto-pause (Mode A). Both detectors are gated by
            // the selected background animation mode; Mode B stops them so
            // the wallpaper keeps animating while another app is focused.
            _fullscreenDetector.FullscreenStateChanged += OnFullscreenChanged;
            _fullscreenDetector.IsEnabled = _settings.IsAutoPauseEnabled;

            _foregroundDetector.ForegroundStateChanged += OnForegroundChanged;
            _foregroundDetector.IsEnabled = _settings.IsAutoPauseEnabled;

            _systemEvents.ShouldPause += OnSystemPause;
            _systemEvents.ShouldResume += OnSystemResume;
            _systemEvents.DisplayChanged += OnDisplayChanged;

            Loaded += OnWindowLoaded;
            Closing += OnWindowClosing;
            StateChanged += OnWindowStateChanged;
            IsVisibleChanged += OnMainWindowVisibleChanged;
            Activated += OnMainWindowActivityChanged;
            Deactivated += OnMainWindowActivityChanged;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            // Register for Explorer restart notification
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source != null)
            {
                _shellRestartMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
                source.AddHook(WndProc);
            }

            _systemEvents.Start();
            UpdateTitleBarTheme();

            // Some WPF/GPU configurations delay ContentRendered when the
            // window is initially transparent. ApplicationIdle is a fallback,
            // not the normal first-paint path.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                (Action)(() =>
                {
                    if (!_firstContentRendered)
                        OnInitialContentRendered(this, EventArgs.Empty);
                }));

            // Second-launch signals can arrive while StartupUri is still
            // constructing the first window. Honour them after first layout.
            if (Application.Current is App app && app.ConsumePendingShowRequest())
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    (Action)RestoreFromSecondaryLaunch);
            }

            // Upgrade old registration commands that always forced a tray
            // launch, even if the current preference wants a visible window.
#if INTONACO_STORE
            _ = RefreshStoreStartupToggleAsync();
#else
            if (StartupManager.IsRegistered)
            {
                string registered = StartupManager.GetRegisteredValue() ?? "";
                bool registeredMinimized = registered.Contains(
                    "--minimized", StringComparison.OrdinalIgnoreCase);
                if (registeredMinimized != _settings.StartMinimized)
                    StartupManager.Register(_settings.StartMinimized);
            }
#endif

            _placementTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _placementTimer.Tick += (_, _) =>
            {
                if (!_isApplied)
                    return;

                _desktop.EnsurePlacement();
                DisableOptionalPreviewIfOverBudget();
                // Sample once per 45 seconds while playing, without forcing a
                // GC or opening an extra decoder. Logs show whether the heavy
                // RAM is in managed objects, native Media Foundation or mpv.
                RuntimeMemoryDiagnostics.LogIfDue(
                    _player is GpuVideoWallpaperPlayer ? "GPU-DirectX" :
                        _player is MpvWallpaperPlayer ? "MPV" :
                        _player is NativeMfPlayWallpaperPlayer ? "Native" : "Windows",
                    _livePreviewEnabled && PreviewMedia.Source != null,
                    _player.NaturalVideoWidth, _player.NaturalVideoHeight,
                    _player is MpvWallpaperPlayer mpv
                        ? mpv.HelperWorkingSetBytes : 0);
            };
            UpdateBackgroundTimers();
        }

        private void OnInitialContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= OnInitialContentRendered;
            _firstContentRendered = true;

            bool restore = _pendingWindowRestore;
            _pendingWindowRestore = false;
            bool recovering = Application.Current is App
                { LastSessionEndedUnexpectedly: true };
            bool silent = _startMinimized && !restore && !recovering;
            _startMinimized = false;
            if (recovering)
            {
                // Show controls rather than silently re-creating the wallpaper
                // after a crashed/killed process or machine freeze.
                ShowInTaskbar = true;
                DebugLog.Write("Unclean previous session: entering safe startup.");
            }

            // A fully constructed and first-painted WPF window is safe to
            // hide and restore. SourceInitialized is too early.
            if (silent)
            {
                Hide();
                ShowInTaskbar = false;
            }

            Opacity = 1;
            if (restore)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    (Action)ShowMainWindow);
            }

            ScheduleStartupWallpaperAfterPaint();
        }

        private void ScheduleStartupWallpaperAfterPaint()
        {
            if (Application.Current is App { LastSessionEndedUnexpectedly: true })
            {
                DebugLog.Write("Safe startup: last wallpaper is NOT auto-played. User may select it manually.");
                StatusText.Text = "Safe startup — automatic wallpaper playback skipped";
                return;
            }

            if (_startupWallpaperScheduled || !_settings.AutoPlay ||
                string.IsNullOrWhiteSpace(_settings.LastWallpaperPath))
                return;

            string path = _settings.LastWallpaperPath;
            if (!File.Exists(path))
            {
                DebugLog.Write("AutoPlay: last wallpaper missing: " + path);
                return;
            }

            _startupWallpaperScheduled = true;
            // Finish the first compositor frame and let Explorer settle.
            // Doing this in Loaded blocked the first WPF paint and produced
            // the black content window visible in the startup recording.
            var restoreTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(900)
            };
            restoreTimer.Tick += async (_, _) =>
            {
                restoreTimer.Stop();

                // Explorer's desktop-layer split performs a bounded Win32
                // wait. Prepare it on a worker thread so the settings UI can
                // paint and respond even during Windows sign-in.
                try
                {
                    await _desktop.PrepareDesktopLayerAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Background desktop preparation: " + ex.Message);
                }

                if (_isExiting || !_settings.AutoPlay || _isApplied ||
                    _wallpaperRequestVersion != 0 ||
                    !string.Equals(_settings.LastWallpaperPath, path,
                        StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path))
                    return;

                DebugLog.Write("Deferred wallpaper restore after first paint: " + path);
                _startupRestoreFile = path;
                ApplyWallpaper(path);
            };
            restoreTimer.Start();
        }

        private void ScheduleStartupAttachmentRetry(string filePath)
        {
            // Explorer may not have created Progman/WorkerW immediately at
            // sign-in. Retry only desktop attachment errors, and never
            // override a newer wallpaper selected by the user.
            if (_isExiting || _startupAttachRetryCount >= 3)
                return;

            int expectedRequestVersion = _wallpaperRequestVersion;
            int attempt = ++_startupAttachRetryCount;
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(attempt * 3)
            };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                if (_isExiting || _isApplied ||
                    _wallpaperRequestVersion != expectedRequestVersion ||
                    !string.Equals(_settings.LastWallpaperPath, filePath,
                        StringComparison.OrdinalIgnoreCase))
                    return;

                try
                {
                    await _desktop.PrepareDesktopLayerAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Startup desktop retry prewarm: " + ex.Message);
                }

                if (_isExiting || _isApplied ||
                    _wallpaperRequestVersion != expectedRequestVersion ||
                    !File.Exists(filePath))
                    return;

                DebugLog.Write($"Retrying desktop attachment after Explorer login: {attempt}/3");
                ApplyWallpaper(filePath);
            };
            timer.Start();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_shellRestartMessage != 0 && msg == (int)_shellRestartMessage)
            {
                // Explorer restarted — reattach wallpaper
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
                {
                    if (_isApplied && _playerWindow != null)
                    {
                        EnsureAttached();
                    }
                });
                handled = true;
            }
            else if (msg == 0x001A) // WM_SETTINGCHANGE (Windows appearance may have changed)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)UpdateTitleBarTheme);
            }
            return IntPtr.Zero;
        }

        private void UpdateTitleBarTheme()
        {
            try
            {
                bool useDark = true;
                if (TryFindResource("WindowBackgroundColor") is Color color)
                {
                    double luminance = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
                    useDark = luminance < 128;
                }
                // The header logo is a device-independent WPF vector.

                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                int value = useDark ? 1 : 0;
                int result = DwmSetWindowAttribute(
                    hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
                if (result != 0)
                {
                    // Early Windows 10 builds exposed the same setting as 19.
                    DwmSetWindowAttribute(
                        hwnd, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
                }

                // Windows 11 can also match the caption itself to the app
                // surface. Unsupported builds simply return an error, which
                // is safe to ignore.
                if (TryFindResource("WindowBackgroundColor") is Color background)
                {
                    int captionColor = background.R | (background.G << 8) | (background.B << 16);
                    DwmSetWindowAttribute(
                        hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));
                }

                if (TryFindResource("TextPrimaryColor") is Color foreground)
                {
                    int textColor = foreground.R | (foreground.G << 8) | (foreground.B << 16);
                    DwmSetWindowAttribute(
                        hwnd, DwmwaTextColor, ref textColor, sizeof(int));
                }
            }
            catch
            {
                // Native caption styling is decorative; never fail the app for it.
            }
        }

        private void InitializeSystemTray()
        {
            // Icon will be set from embedded resource
            try
            {
                var iconUri = new Uri("pack://application:,,,/Assets/intonaco.ico", UriKind.Absolute);
                using var stream = Application.GetResourceStream(iconUri)?.Stream;
                if (stream != null)
                {
                    TrayIcon.Icon = new System.Drawing.Icon(stream);
                }
            }
            catch
            {
                // Will use default icon
            }

            TrayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
        }

        #region Wallpaper Management

        private void OnWindowDragEnter(object sender, DragEventArgs e) => HandleDrag(e);

        private void OnWindowDragOver(object sender, DragEventArgs e) => HandleDrag(e);

        private void OnWindowDragLeave(object sender, DragEventArgs e)
        {
            Point position = e.GetPosition(this);
            if (position.X < 0 || position.Y < 0 ||
                position.X > ActualWidth || position.Y > ActualHeight)
            {
                DropOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void OnWindowDrop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (TryGetDroppedVideo(e, out string? filePath) && filePath != null)
            {
                ApplyWallpaper(filePath);
            }
            else
            {
                ShowError("Drop a video or animated file: MP4, MKV, WebM, FLV, MOV, GIF and more.");
            }
            e.Handled = true;
        }

        private void HandleDrag(DragEventArgs e)
        {
            bool hasVideo = TryGetDroppedVideo(e, out _);
            e.Effects = hasVideo ? DragDropEffects.Copy : DragDropEffects.None;
            DropOverlay.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private static bool TryGetDroppedVideo(DragEventArgs e, out string? filePath)
        {
            filePath = null;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
                e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            {
                return false;
            }

            filePath = files.FirstOrDefault(path =>
                File.Exists(path) && MediaFormatProbe.IsSupportedExtension(path));
            return filePath != null;
        }

        private void OnRecentWallpaperClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string filePath)
                return;

            if (File.Exists(filePath))
            {
                ApplyWallpaper(filePath);
                return;
            }

            _settings.RecentWallpaperPaths.RemoveAll(path =>
                string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            RefreshRecentWallpapers();
            ShowError("That recent wallpaper no longer exists, so it was removed from the list.");
        }

        private void OnClearRecentWallpapers(object sender, RoutedEventArgs e)
        {
            _settings.RecentWallpaperPaths.Clear();
            _settings.Save();
            RefreshRecentWallpapers();
        }

        private void RefreshRecentWallpapers()
        {
            _settings.Normalize();
            _recentWallpapers.Clear();

            foreach (string path in _settings.RecentWallpaperPaths.Where(File.Exists))
            {
                _recentWallpapers.Add(new RecentWallpaperItem
                {
                    FullPath = path,
                    FileName = System.IO.Path.GetFileName(path),
                    Meta = BuildRecentMeta(path)
                });
            }

            bool hasRecents = _recentWallpapers.Count > 0;
            RecentEmptyText.Visibility = hasRecents ? Visibility.Collapsed : Visibility.Visible;
            ClearRecentsButton.IsEnabled = hasRecents;
        }

        private string BuildRecentMeta(string path)
        {
            string meta = $"{System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} • {FormatFileSize(path)}";

            if (string.Equals(path, _activeOriginalFile, StringComparison.OrdinalIgnoreCase))
            {
                if (_player.NaturalVideoWidth > 0 && _player.NaturalVideoHeight > 0)
                    meta += $" • {_player.NaturalVideoWidth}×{_player.NaturalVideoHeight}";
                if (_player.Duration > TimeSpan.Zero)
                    meta += $" • {FormatDuration(_player.Duration)}";
            }

            return meta;
        }

        private void OnOpenFileLocation(object sender, RoutedEventArgs e)
        {
            string? path = _activeOriginalFile ?? _settings.LastWallpaperPath;
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else if (!string.IsNullOrEmpty(path) &&
                         Directory.Exists(System.IO.Path.GetDirectoryName(path)))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe",
                        $"\"{System.IO.Path.GetDirectoryName(path)}\"")
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                ShowError($"Could not open the file location: {ex.Message}");
            }
        }

        private void OnOpenLogs(object sender, RoutedEventArgs e)
        {
            try
            {
                string? folder = System.IO.Path.GetDirectoryName(DebugLog.LogFilePath);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                ShowError($"Could not open the log folder: {ex.Message}");
            }
        }

        private void OnChooseFile(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose wallpaper — Intonaco",
                Filter = VideoDialogFilter,
                CheckFileExists = true,
                Multiselect = false
            };

            if (!string.IsNullOrEmpty(_settings.LastWallpaperPath))
            {
                var dir = Path.GetDirectoryName(_settings.LastWallpaperPath);
                if (dir != null && Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
            }

            if (dialog.ShowDialog(this) == true)
            {
                ApplyWallpaper(dialog.FileName);
            }
        }

        private async void ApplyWallpaper(
            string filePath,
            bool startPaused = false,
            TimeSpan pausedPosition = default)
        {
            ClearError();

            // Validate before tearing down a wallpaper that is currently
            // working, so a bad file never leaves the desktop empty.
            if (!MediaFormatProbe.TryValidate(filePath, out string probeError))
            {
                DebugLog.Write($"ApplyWallpaper rejected '{filePath}': {probeError}");
                NotifyPlaybackError(probeError);
                return;
            }

            // Retain a known-good wallpaper until the replacement has opened.
            _rollbackOriginalPath = null;
            if (_lastVideoOpened && _isApplied &&
                _activeOriginalFile != null &&
                !string.Equals(_activeOriginalFile, filePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _rollbackOriginalPath = _activeOriginalFile;
                _rollbackWasPaused =
                    _player.CurrentState == WallpaperPlayer.PlaybackState.Paused;
                _rollbackPosition = _player.Position;
            }

            // Our native engine performs container inspection and lossless
            // aspect-metadata repair. It does not execute or download codecs.
            _preparationCancellation?.Cancel();
            var operation = new CancellationTokenSource();
            _preparationCancellation = operation;
            int requestVersion = ++_wallpaperRequestVersion;
            PreparedWallpaper prepared;
            try
            {
                prepared = await NativeMediaEngine.PrepareAsync(
                    filePath, operation.Token);
                // Shell window discovery and 0x052C requests can take seconds
                // on an overloaded Explorer. Never run them on the WPF UI
                // thread, and cancel older requests when users switch files.
                await _desktop.PrepareDesktopLayerAsync(operation.Token);
            }
            catch (OperationCanceledException)
            {
                if (requestVersion == _wallpaperRequestVersion && !_isExiting)
                    ResetStatusAfterPreparation();
                return;
            }
            catch (Exception ex)
            {
                if (requestVersion == _wallpaperRequestVersion && !_isExiting)
                {
                    ResetStatusAfterPreparation();
                    NotifyPlaybackError("Could not prepare this video: " + ex.Message);
                }
                return;
            }
            finally
            {
                if (ReferenceEquals(_preparationCancellation, operation))
                    _preparationCancellation = null;
                operation.Dispose();
            }

            if (_isExiting || requestVersion != _wallpaperRequestVersion)
                return;

            // Do not keep showing an old decoder when applying a new file.
            StopWallpaper();
            _sourceWidth = prepared.SourceWidth;
            _sourceHeight = prepared.SourceHeight;
            _activeOriginalFile = filePath;
            if (prepared.CorrectedAspect)
                DebugLog.Write("MP4 nonstandard pixel aspect normalized in cache; original untouched: " + filePath);


            // Create player window if needed
            if (_playerWindow == null)
            {
                _playerWindow = new PlayerWindow();
                var playerHost = _player.CreateHost();
                _playerWindow.PlayerGrid.Children.Add(playerHost);
                _playerWindow.Show();
            }

            // Attach to desktop
            if (!_desktop.Attach(_playerWindow))
            {
                string reason = _desktop.LastError ?? "Failed to attach to desktop.";
                DebugLog.Write($"ApplyWallpaper: attach failed for '{filePath}': {reason}");
                // Close the (already shown) player window — otherwise it stays
                // on screen as a floating black rectangle.
                StopWallpaper();
                ShowError($"{reason} Details logged to %LocalAppData%\\Intonaco\\debug.log");
                if (string.Equals(_startupRestoreFile, filePath,
                    StringComparison.OrdinalIgnoreCase))
                    ScheduleStartupAttachmentRetry(filePath);
                return;
            }

            // Start playback
            if (!_player.Play(prepared.PlaybackPath))
            {
                return; // Error event will fire
            }

            _player.SetMute(_settings.IsMuted);
            _player.Volume = _settings.Volume;
            _player.TargetFps = _settings.TargetFps;
            _player.StretchMode = ParseFitMode(_settings.WallpaperFit);
            _player.PlaybackSpeed = _settings.PlaybackSpeed;
            _player.LoopPlayback = _settings.LoopPlayback;

            if (startPaused)
            {
                // Explorer-restart recovery. Keep rendering through the live
                // media surface at the saved position; no static frame is
                // carried across from the destroyed player window.
                _player.PauseAt(pausedPosition);
                _pausedByUser = !(_pausedByFullscreen || _pausedByForeground || _pausedBySystem);
            }
            else
            {
                _pausedByUser = false;
            }

            _isApplied = true;
            UpdateBackgroundTimers();

            // Update settings and recents
            _settings.RememberWallpaper(filePath);
            _settings.Save();

            // Update UI
            UpdateFileInfo(filePath);
            UpdatePreview(prepared.PlaybackPath);
            RefreshRecentWallpapers();
        }

        private void ResetStatusAfterPreparation()
        {
            StatusText.Text = _player.CurrentState switch
            {
                WallpaperPlayer.PlaybackState.Playing => "Playing",
                WallpaperPlayer.PlaybackState.Paused => "Paused",
                _ => "Stopped"
            };
        }

        private void StopWallpaper()
        {
            _player.Stop();
            _lastVideoOpened = false;
            _activeOriginalFile = null;
            _sourceWidth = 0;
            _sourceHeight = 0;
            _desktop.Detach();
            _isApplied = false;
            _pausedByUser = false;
            _pausedByFullscreen = false;
            _pausedByForeground = false;
            _pausedBySystem = false;
            UpdateBackgroundTimers();

            ClearPreview();

            if (_playerWindow != null)
            {
                _playerWindow.PlayerGrid.Children.Clear();
                _playerWindow.Close();
                _playerWindow = null;
            }
        }

        private void UpdateFileInfo(string filePath)
        {
            FileNameText.Text = Path.GetFileName(filePath);
            FileNameText.ToolTip = filePath;

            try
            {
                var fi = new FileInfo(filePath);
                _fileInfoBase =
                    $"{fi.Extension.TrimStart('.').ToUpperInvariant()} • {FormatFileSize(fi.Length)}";
                FileInfoText.Text = _fileInfoBase;
                OpenFolderButton.IsEnabled = fi.Exists;
            }
            catch
            {
                _fileInfoBase = string.Empty;
                FileInfoText.Text = "";
                OpenFolderButton.IsEnabled = false;
            }
        }

        private static string FormatFileSize(string filePath)
        {
            try
            {
                return FormatFileSize(new FileInfo(filePath).Length);
            }
            catch
            {
                return "Unknown size";
            }
        }

        private static string FormatFileSize(long length)
        {
            return length switch
            {
                < 1024 => $"{length} B",
                < 1048576 => $"{length / 1024.0:F1} KB",
                < 1073741824 => $"{length / 1048576.0:F1} MB",
                _ => $"{length / 1073741824.0:F2} GB"
            };
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"m\:ss");
        }

        // A separate video decoder is useful only while the user is actually
        // interacting with Intonaco. Release it when switching applications.
        private bool IsPreviewWindowVisible =>
            IsVisible && WindowState != WindowState.Minimized && IsActive;

        private void OnMainWindowActivityChanged(object? sender, EventArgs e)
        {
            if (_isExiting || !IsLoaded)
                return;

            RefreshPreviewForCurrentState();
            UpdateSeekTimer();
        }

        // Keep the media preview in a balanced 16:9 frame instead of a thin
        // panoramic strip when the app is maximized. This changes only the
        // settings window, never the desktop wallpaper's original aspect.
        private void OnPreviewPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged || e.NewSize.Width <= 0)
                return;

            double desiredHeight = Math.Clamp(e.NewSize.Width * 9.0 / 16.0, 230, 596.25);
            if (Math.Abs(PreviewPanel.Height - desiredHeight) > 0.5)
                PreviewPanel.Height = desiredHeight;
        }

        private void UpdatePreview(string filePath)
        {
            // Keep one video decoder by default: use an OS thumbnail until
            // the user explicitly opts into a moving preview.
            _previewFilePath = filePath;
            _livePreviewEnabled = false;
            _thumbnailRequestedFor = null;
            _thumbnailImage = null;
            ++_previewThumbnailVersion;
            PreviewModeButton.Content = "Play live preview";
            PreviewModeButton.Visibility = _player is MpvWallpaperPlayer
                or NativeMfPlayWallpaperPlayer or GpuVideoWallpaperPlayer
                ? Visibility.Collapsed : Visibility.Visible;
            RefreshPreviewForCurrentState();
        }

        private void ClearPreview()
        {
            _previewFilePath = null;
            _livePreviewEnabled = false;
            _thumbnailImage = null;
            _thumbnailRequestedFor = null;
            ++_previewThumbnailVersion;
            PreviewModeButton.Visibility = Visibility.Collapsed;
            PreviewModeButton.Content = "Play live preview";
            _previewNeedsPositionSync = false;
            ReleasePreviewPipeline();
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            PreviewStatusText.Text = "No video";
        }

        /// <summary>
        /// Keeps the second preview decoder alive only when the preview can
        /// actually be seen. Pausing stops its clock in place - the same
        /// rendering surface keeps showing the last frame - and sending the
        /// main window to the tray still closes the preview pipeline.
        /// </summary>
        private void RefreshPreviewForCurrentState()
        {
            // Shell thumbnails also work when the desktop uses MPV;
            // never start an additional Windows video decoder for MPV files.
            if (_player is MpvWallpaperPlayer or NativeMfPlayWallpaperPlayer
                or GpuVideoWallpaperPlayer)
            {
                _livePreviewEnabled = false;
                PreviewModeButton.Visibility = Visibility.Collapsed;
                ShowStaticPreview();
                return;
            }
            if (_previewFilePath == null)
            {
                ReleasePreviewPipeline();
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                return;
            }

            if (!_livePreviewEnabled)
            {
                ShowStaticPreview();
                return;
            }

            PreviewPlaceholder.Visibility = Visibility.Collapsed;

            if (!IsPreviewWindowVisible)
            {
                // Releasing the optional decoder on focus loss is safe;
                // the thumbnail remains available with no black flash.
                _livePreviewEnabled = false;
                PreviewModeButton.Content = "Play live preview";
                ShowStaticPreview();
                return;
            }

            switch (_player.CurrentState)
            {
                case WallpaperPlayer.PlaybackState.Playing:
                    StartPreview();
                    break;
                case WallpaperPlayer.PlaybackState.Paused:
                    ShowPausedPreview();
                    break;
                default:
                    ReleasePreviewPipeline();
                    PreviewImage.Source = null;
                    PreviewImage.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        /// <summary>
        /// A static Windows video thumbnail prevents a second media decoder
        /// from running continuously beside the desktop player. It also
        /// keeps the UI populated if the optional video preview cannot paint.
        /// </summary>
        private void ShowStaticPreview()
        {
            if (PreviewMedia.Source != null)
                ReleasePreviewPipeline();

            if (_previewFilePath == null)
            {
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                return;
            }

            if (_thumbnailImage != null)
            {
                PreviewImage.Source = _thumbnailImage;
                PreviewImage.Visibility = Visibility.Visible;
                PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
                PreviewPlaceholder.Visibility = Visibility.Visible;
                PreviewHintText.Text = _thumbnailRequestedFor == null
                    ? "Loading video thumbnail..."
                    : "Wallpaper plays on your desktop";
            }

            // Explorer thumbnail providers can load a full 4K decoder
            // transiently inside our process, even when the 480x270 image
            // is never seen. Never make that request while Intonaco is in
            // the tray or inactive: load only when the settings UI is open.
            // Activation already calls RefreshPreviewForCurrentState().
            if (!IsPreviewWindowVisible)
                return;

            // The OS thumbnail provider may open another 4K media decoder.
            // During a GPU wallpaper switch the previous decoder may still
            // be cleaning up and the new one is just starting. Never start
            // a third decode before the newly selected wallpaper has opened.
            // OnMediaOpened schedules the deferred preview once ready.
            if (_player is GpuVideoWallpaperPlayer && !_lastVideoOpened)
                return;

            if (!string.Equals(_thumbnailRequestedFor, _previewFilePath,
                StringComparison.OrdinalIgnoreCase))
            {
                string path = _previewFilePath;
                _thumbnailRequestedFor = path;
                int requestId = _previewThumbnailVersion;
                _ = LoadStaticPreviewAsync(path, requestId);
            }
        }

        private async System.Threading.Tasks.Task LoadStaticPreviewAsync(
            string filePath, int requestId)
        {
            BitmapSource? image = null;
            try
            {
                // When GPU playback is active, only retrieve an existing
                // Windows thumbnail. A cache miss is cheaper than launching
                // an entirely separate 4K decode for a 480px preview.
                image = await VideoThumbnail.LoadAsync(filePath,
                    cachedThumbnailOnly: _player is GpuVideoWallpaperPlayer);
            }
            catch (Exception ex)
            {
                DebugLog.Write("Thumbnail extraction failed: " + ex.Message);
            }

            if (_isExiting || requestId != _previewThumbnailVersion ||
                !string.Equals(_previewFilePath, filePath,
                    StringComparison.OrdinalIgnoreCase))
                return;

            _thumbnailImage = image;
            if (!_livePreviewEnabled)
                ShowStaticPreview();
        }

        // The optional in-app video preview creates a SECOND decoder. It is
        // never more important than the full-resolution desktop playback.
        private const long OptionalPreviewPrivateMemoryBudget = 384L * 1024 * 1024;

        private bool IsOptionalPreviewOverBudget()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                return process.PrivateMemorySize64 >= OptionalPreviewPrivateMemoryBudget;
            }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }

        private void DisableOptionalPreviewIfOverBudget()
        {
            if (!_livePreviewEnabled || !IsOptionalPreviewOverBudget())
                return;

            _livePreviewEnabled = false;
            ReleasePreviewPipeline();
            ShowStaticPreview();
            PreviewModeButton.Content = "Play live preview";
            PreviewStatusText.Text = "Still preview — memory saver";
            DebugLog.Write("Optional second video decoder disabled after exceeding 384 MiB private memory.");
        }

        private void OnToggleLivePreview(object sender, RoutedEventArgs e)
        {
            if (_previewFilePath == null ||
                _player is MpvWallpaperPlayer or NativeMfPlayWallpaperPlayer
                    or GpuVideoWallpaperPlayer)
                return;

            if (!_livePreviewEnabled && IsOptionalPreviewOverBudget())
            {
                PreviewStatusText.Text = "Still preview — memory saver";
                DebugLog.Write("Optional second video decoder skipped: already above 384 MiB private memory.");
                return;
            }

            _livePreviewEnabled = !_livePreviewEnabled;
            PreviewModeButton.Content = _livePreviewEnabled
                ? "Use still preview" : "Play live preview";
            RefreshPreviewForCurrentState();
        }

        private void OnPreviewMediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            DebugLog.Write("Optional preview decoder failed: " +
                e.ErrorException?.Message);
            _livePreviewEnabled = false;
            PreviewModeButton.Content = "Play live preview";
            ShowStaticPreview();
        }

        private void StartPreview()
        {
            if (!_livePreviewEnabled || _previewFilePath == null ||
                !IsPreviewWindowVisible ||
                _player.CurrentState != WallpaperPlayer.PlaybackState.Playing)
            {
                return;
            }

            try
            {
                bool sourceChanged = PreviewMedia.Source == null ||
                    !string.Equals(
                        PreviewMedia.Source.LocalPath,
                        _previewFilePath,
                        StringComparison.OrdinalIgnoreCase);

                if (sourceChanged)
                {
                    ReleasePreviewPipeline();
                    PreviewMedia.Source = new Uri(_previewFilePath, UriKind.Absolute);
                    _previewNeedsPositionSync = true;
                }

                PreviewMedia.Stretch = _player.StretchMode;
                PreviewMedia.SpeedRatio = _player.PlaybackSpeed;
                ApplyPreviewFrameRateCap(_settings.TargetFps);
                PreviewMedia.IsMuted = true;
                // A resume must not reset the preview: the same source is
                // simply told to play again from its current frame.
                PreviewMedia.Play();
                PreviewMedia.Visibility = Visibility.Visible;
                // Keep the cached thumbnail over the decoder until MediaOpened.
                // This prevents the large black rectangle during initialization.
                if (_thumbnailImage == null)
                {
                    PreviewImage.Visibility = Visibility.Collapsed;
                    PreviewPlaceholder.Visibility = Visibility.Visible;
                }
                else
                {
                    PreviewImage.Source = _thumbnailImage;
                    PreviewImage.Visibility = Visibility.Visible;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
                ClearPreview();
            }
        }

        private void ShowPausedPreview()
        {
            // The preview uses the same rule as the desktop: stop animation
            // updates, keep the rendering surface. Photographing a WPF
            // MediaElement into a bitmap and closing it is exactly what put
            // black bands/blocks around a paused background.
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;

            if (PreviewMedia.Source != null)
            {
                try
                {
                    PreviewMedia.Stretch = _player.StretchMode;
                    PreviewMedia.Pause();
                    PreviewMedia.Visibility = Visibility.Visible;
                }
                catch
                {
                    StartPausedPreview();
                }
                return;
            }

            // The preview pipeline was released while the window was hidden;
            // reopen it at the desktop's paused position, still paused.
            StartPausedPreview();
        }

        /// <summary>
        /// Opens the preview source without starting its clock. MediaOpened
        /// seeks it to the desktop position and pauses it, so a paused
        /// wallpaper never animates in the preview either.
        /// </summary>
        private void StartPausedPreview()
        {
            if (_previewFilePath == null || !IsPreviewWindowVisible)
                return;

            try
            {
                ReleasePreviewPipeline();
                PreviewMedia.Source = new Uri(_previewFilePath, UriKind.Absolute);
                _previewNeedsPositionSync = true;
                PreviewMedia.Stretch = _player.StretchMode;
                PreviewMedia.SpeedRatio = _player.PlaybackSpeed;
                ApplyPreviewFrameRateCap(_settings.TargetFps);
                PreviewMedia.IsMuted = true;
                PreviewMedia.Visibility = Visibility.Visible;
                PreviewMedia.Pause();
            }
            catch
            {
                ReleasePreviewPipeline();
            }
        }

        private void ReleasePreviewPipeline()
        {
            try
            {
                PreviewMedia.Stop();
                PreviewMedia.Source = null;
                PreviewMedia.Close();
            }
            catch { }

            PreviewMedia.Visibility = Visibility.Collapsed;
        }

        private void OnPreviewMediaOpened(object sender, RoutedEventArgs e)
        {
            if (!_livePreviewEnabled)
                return;

            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            if (_player.CurrentState == WallpaperPlayer.PlaybackState.Paused)
            {
                // A preview reopened while paused must show the desktop's
                // frozen frame and must never start animating.
                try
                {
                    if (_previewNeedsPositionSync &&
                        _player.Position > TimeSpan.FromMilliseconds(500))
                    {
                        PreviewMedia.Position = _player.Position;
                    }
                    PreviewMedia.Pause();
                }
                catch
                {
                    // Keeping the first frame is preferable to a black preview.
                }

                _previewNeedsPositionSync = false;
                PreviewMedia.Visibility = IsPreviewWindowVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                return;
            }

            if (!_previewNeedsPositionSync)
                return;

            _previewNeedsPositionSync = false;

            if (!IsPreviewWindowVisible ||
                _player.CurrentState != WallpaperPlayer.PlaybackState.Playing)
            {
                return;
            }

            try
            {
                TimeSpan desktopPosition = _player.Position;
                if (desktopPosition > TimeSpan.FromMilliseconds(500) &&
                    (PreviewMedia.Position - desktopPosition).Duration() >
                        TimeSpan.FromMilliseconds(500))
                {
                    PreviewMedia.Position = desktopPosition;
                }
            }
            catch
            {
                // The two MediaElements do not need sample-accurate sync.
            }
        }

        private void OnPreviewMediaEnded(object sender, RoutedEventArgs e)
        {
            if (!IsPreviewWindowVisible ||
                _player.CurrentState != WallpaperPlayer.PlaybackState.Playing ||
                !_settings.LoopPlayback)
            {
                return;
            }

            try
            {
                PreviewMedia.Position = TimeSpan.Zero;
                PreviewMedia.Play();
            }
            catch { }
        }

        private void OnMainWindowVisibleChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            if (_isExiting)
                return;
            RefreshPreviewForCurrentState();
            UpdateSeekTimer();
        }

        /// <summary>
        /// Sample the native playhead only while the control window is visible.
        /// There is no per-frame listener or extra decoder just for the slider.
        /// </summary>
        private void UpdateSeekTimer()
        {
            bool active = !_isExiting && IsPreviewWindowVisible &&
                _player.CurrentState == WallpaperPlayer.PlaybackState.Playing;
            if (!active)
            {
                _seekUpdateTimer?.Stop();
                RefreshSeekProgress();
                return;
            }

            if (_seekUpdateTimer == null)
            {
                _seekUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _seekUpdateTimer.Tick += (_, _) => RefreshSeekProgress();
            }

            RefreshSeekProgress();
            if (!_seekUpdateTimer.IsEnabled)
                _seekUpdateTimer.Start();
        }

        private void RefreshSeekProgress()
        {
            if (!IsLoaded || SeekSlider == null)
                return;

            TimeSpan duration = _player.Duration;
            TimeSpan position = _player.Position;
            bool canSeek = duration > TimeSpan.Zero &&
                _player.CurrentState != WallpaperPlayer.PlaybackState.Stopped &&
                _player.CurrentState != WallpaperPlayer.PlaybackState.Error;

            _updatingSeekSlider = true;
            try
            {
                SeekSlider.IsEnabled = canSeek;
                if (!SeekSlider.IsMouseCaptureWithin && _seekCommitTimer?.IsEnabled != true)
                    SeekSlider.Value = canSeek
                        ? Math.Clamp(100.0 * position.TotalSeconds /
                            duration.TotalSeconds, 0.0, 100.0)
                        : 0;
                SeekElapsedText.Text = FormatDuration(canSeek ? position : TimeSpan.Zero);
                SeekDurationText.Text = FormatDuration(canSeek ? duration : TimeSpan.Zero);
            }
            finally
            {
                _updatingSeekSlider = false;
            }
        }

        private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressEvents || _updatingSeekSlider ||
                _player.Duration <= TimeSpan.Zero)
                return;

            // Debounce media seeks so moving the thumb does not ask the 4K
            // hardware decoder to jump on every pointer event.
            _pendingSeekPosition = TimeSpan.FromSeconds(
                _player.Duration.TotalSeconds * Math.Clamp(e.NewValue, 0, 100) / 100);
            SeekElapsedText.Text = FormatDuration(_pendingSeekPosition);

            if (_seekCommitTimer == null)
            {
                _seekCommitTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(240)
                };
                _seekCommitTimer.Tick += (_, _) =>
                {
                    _seekCommitTimer.Stop();
                    if (_player.CurrentState == WallpaperPlayer.PlaybackState.Stopped)
                        return;

                    _player.Seek(_pendingSeekPosition);
                    try
                    {
                        if (PreviewMedia.Source != null)
                            PreviewMedia.Position = _pendingSeekPosition;
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write("Preview seek failed: " + ex.Message);
                    }
                    RefreshSeekProgress();
                };
            }

            _seekCommitTimer.Stop();
            _seekCommitTimer.Start();
        }

        private void UpdateBackgroundTimers()
        {
            bool shouldDetect = _isApplied &&
                                _settings.IsAutoPauseEnabled &&
                                (_player.CurrentState == WallpaperPlayer.PlaybackState.Playing ||
                                 _pausedByFullscreen || _pausedByForeground);

            if (shouldDetect)
            {
                _fullscreenDetector.IsEnabled = _settings.IsAutoPauseEnabled;
                _foregroundDetector.IsEnabled = _settings.IsAutoPauseEnabled;
                _fullscreenDetector.Start();
                _foregroundDetector.Start();
            }
            else
            {
                _fullscreenDetector.Stop();
                _foregroundDetector.Stop();
            }

            if (_placementTimer == null)
                return;

            if (!_isApplied)
            {
                _placementTimer.Stop();
                return;
            }

            _placementTimer.Interval =
                _player.CurrentState == WallpaperPlayer.PlaybackState.Playing
                    ? TimeSpan.FromSeconds(10)
                    : TimeSpan.FromSeconds(15);

            if (!_placementTimer.IsEnabled)
                _placementTimer.Start();
        }

        private void SaveSettingsSoon()
        {
            if (_settingsSaveTimer == null)
            {
                _settingsSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                _settingsSaveTimer.Tick += (_, _) =>
                {
                    _settingsSaveTimer.Stop();
                    _settings.Save();
                };
            }

            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        }

        private void FlushSettingsSave()
        {
            if (_settingsSaveTimer?.IsEnabled == true)
            {
                _settingsSaveTimer.Stop();
                _settings.Save();
            }
        }

        #endregion

        #region Playback Controls

        private void OnPlayPause(object sender, RoutedEventArgs e)
        {
            if (_player.CurrentState == WallpaperPlayer.PlaybackState.Playing)
            {
                // Explicit manual pause. It is recorded separately from focus
                // auto-pause so no focus event can resume it behind the
                // user's back, in either animation mode.
                _pausedByUser = true;
                _player.Pause();
            }
            else if (_player.CurrentState == WallpaperPlayer.PlaybackState.Paused)
            {
                _pausedByUser = false;
                _player.Resume();
                _pausedByFullscreen = false;
                _pausedByForeground = false;
            }
            else if (!string.IsNullOrEmpty(_settings.LastWallpaperPath) &&
                     File.Exists(_settings.LastWallpaperPath))
            {
                _pausedByUser = false;
                ApplyWallpaper(_settings.LastWallpaperPath);
            }
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            ++_wallpaperRequestVersion; // Cancel an in-flight async apply.
            _preparationCancellation?.Cancel();
            _rollbackOriginalPath = null;
            _seekCommitTimer?.Stop();
            StopWallpaper();
        }

        private void OnToggleMute(object sender, RoutedEventArgs e)
        {
            bool willUnmute = _player.IsMuted || _player.Volume <= 0.001;
            _player.ToggleMute();
            if (willUnmute && _player.IsMuted)
            {
                // Volume was effectively muted by the slider rather than the
                // player's mute flag; ToggleMute flipped the flag the wrong way.
                _player.SetMute(false);
            }
            _settings.IsMuted = _player.IsMuted;

            if (willUnmute && VolumeSlider.Value <= 0)
            {
                VolumeSlider.Value = 50; // OnVolumeChanged saves the volume.
            }
            else
            {
                _settings.Save();
            }
            UpdateMuteButton();
        }

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateVolumeLabel();
            if (_suppressEvents) return;

            _player.Volume = e.NewValue / 100.0;
            if (e.NewValue > 0 && _player.IsMuted)
                _player.SetMute(false);

            _settings.Volume = _player.Volume;
            _settings.IsMuted = _player.IsMuted;
            SaveSettingsSoon();
            UpdateMuteButton();
        }

        private void UpdateVolumeLabel()
        {
            VolumeText.Text = $"{Math.Round(VolumeSlider.Value):0}%";
        }

        #endregion

        #region Settings

        /// <summary>
        /// Persists the dedicated background-animation mode. Mode A (default)
        /// pauses animation updates while another app is focused; Mode B
        /// keeps the animation running. Changing the mode never touches a
        /// manual pause and never changes the rendered background.
        /// </summary>
        private void OnBackgroundAnimationModeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;

            if (BackgroundAnimationComboBox.SelectedItem is not ComboBoxItem item ||
                item.Tag is not string mode)
            {
                return;
            }

            _settings.BackgroundAnimationMode =
                AppSettings.NormalizeBackgroundAnimationMode(mode);
            _settings.Normalize(); // keep the legacy flags in sync for old builds
            _settings.Save();

            bool autoPauseEnabled = _settings.IsAutoPauseEnabled;
            _fullscreenDetector.IsEnabled = autoPauseEnabled;
            _foregroundDetector.IsEnabled = autoPauseEnabled;

            if (!autoPauseEnabled)
            {
                // Mode B: release any focus-owned pause. A user-owned pause
                // must remain paused, so it is explicitly excluded.
                bool focusOwnedPause = _pausedByForeground || _pausedByFullscreen;
                _pausedByForeground = false;
                _pausedByFullscreen = false;

                if (PlaybackFocusPolicy.CanApplyContinueMode(
                        _pausedByUser,
                        focusOwnedPause,
                        _pausedBySystem,
                        _player.CurrentState == WallpaperPlayer.PlaybackState.Paused))
                {
                    _player.Resume();
                }
            }

            UpdateBackgroundTimers();
            DebugLog.Write(
                $"Background animation mode: {_settings.BackgroundAnimationMode} " +
                $"(manual pause={_pausedByUser}).");
        }

        private void OnStartupChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;

#if INTONACO_STORE
            _ = ApplyStoreStartupPreferenceAsync(StartWithWindowsToggle.IsChecked == true);
#else
            if (StartWithWindowsToggle.IsChecked == true)
                StartupManager.Register(_settings.StartMinimized);
            else
                StartupManager.Unregister();

            _settings.StartWithWindows = StartWithWindowsToggle.IsChecked == true;
            _settings.Save();
#endif
        }

#if INTONACO_STORE
        private async System.Threading.Tasks.Task RefreshStoreStartupToggleAsync()
        {
            try
            {
                bool enabled = await StoreStartupManager.IsEnabledAsync();
                if (_isExiting) return;
                _suppressEvents = true;
                StartWithWindowsToggle.IsChecked = enabled;
                _settings.StartWithWindows = enabled;
                _settings.Save();
            }
            catch (Exception ex)
            {
                DebugLog.Write("Store startup status could not be read: " + ex.Message);
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        private async System.Threading.Tasks.Task ApplyStoreStartupPreferenceAsync(bool enabled)
        {
            try
            {
                bool actual = await StoreStartupManager.SetEnabledAsync(enabled);
                _suppressEvents = true;
                StartWithWindowsToggle.IsChecked = actual;
                _settings.StartWithWindows = actual;
                _settings.Save();
                if (actual != enabled)
                    ShowError("Windows has disabled startup for Intonaco Wallpaper. Enable it in Settings > Apps > Startup.");
            }
            catch (Exception ex)
            {
                DebugLog.Write("Store startup preference update failed: " + ex.Message);
                ShowError("Could not update Windows startup. Check Settings > Apps > Startup.");
            }
            finally
            {
                _suppressEvents = false;
            }
        }
#endif

        private void OnChooseMpvExecutable(object sender, RoutedEventArgs e)
        {
            var picker = new OpenFileDialog
            {
                Title = "Locate your installed mpv.exe",
                Filter = "MPV player (mpv.exe)|mpv.exe|Executables (*.exe)|*.exe",
                CheckFileExists = true,
                Multiselect = false
            };

            if (picker.ShowDialog(this) != true)
                return;

            if (!string.Equals(Path.GetFileName(picker.FileName), "mpv.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Choose an installed MPV player named mpv.exe.");
                return;
            }

            try
            {
                _settings.MpvExecutablePath = Path.GetFullPath(picker.FileName);
                MpvWallpaperPlayer.ConfiguredExecutablePath =
                    _settings.MpvExecutablePath;
                _settings.Save();
                MpvPathStatusText.Text = "MPV located. Select MPV under Video engine to use it.";
                DebugLog.Write("Optional MPV executable configured by user.");
            }
            catch (Exception ex)
            {
                ShowError("Could not configure MPV: " + ex.Message);
            }
        }

        private void OnBackendChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents ||
                BackendComboBox.SelectedItem is not ComboBoxItem item ||
                item.Tag is not string candidate)
                return;

            string requested = AppSettings.NormalizeBackend(candidate);
            if (requested == _settings.PlaybackBackend)
                return;

            if (requested == "MPV" &&
                !MpvWallpaperPlayer.TryLocateExecutable(out _))
            {
                _suppressEvents = true;
                try { SelectComboByTag(BackendComboBox, _settings.PlaybackBackend); }
                finally { _suppressEvents = false; }
                ShowError("MPV is optional and not bundled. Install mpv from mpv.io, " +
                    "put mpv.exe beside Intonaco.exe or add it to PATH, then choose MPV again.");
                return;
            }

            string? activeFile = _activeOriginalFile;
            bool wasPaused = _player.CurrentState == WallpaperPlayer.PlaybackState.Paused;
            TimeSpan position = _player.Position;

            // Release one backend fully before activating the next. Never
            // retain two desktop decoders or orphaned MPV processes.
            ++_wallpaperRequestVersion;
            _preparationCancellation?.Cancel();
            StopWallpaper();
            UnwirePlayerEvents();
            _player.Dispose();
            _player = CreatePlayer(requested);
            WirePlayerEvents();

            _settings.PlaybackBackend = requested;
            ApplyPlayerSettings();
            UpdateBackendInterface();
            _settings.Save();
            RefreshPreviewForCurrentState();
            UpdateSeekTimer();

            if (!string.IsNullOrEmpty(activeFile) && File.Exists(activeFile))
                ApplyWallpaper(activeFile, startPaused: wasPaused, pausedPosition: position);
        }

        private void OnFpsChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;

            if (FpsComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag && int.TryParse(tag, out int fps))
            {
                _settings.TargetFps = fps;
                _player.TargetFps = fps;
                _settings.Save();
                DebugLog.Write(
                    $"FPS preference saved as {fps}; native decoder follows the source cadence (no artificial FPS cap).");
            }
        }

        /// <summary>
        /// WPF MediaElement does not expose a safe frame-rate limiter. This
        /// compatibility method is intentionally a no-op: applying the
        /// Timeline attached property throws an InvalidCastException.
        /// </summary>
        private void ApplyPreviewFrameRateCap(int fps)
        {
            // Source cadence remains unchanged; no hidden re-encoding.
        }

        private void OnFitChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;

            if (FitComboBox.SelectedItem is ComboBoxItem item && item.Tag is string fit)
            {
                _settings.WallpaperFit = AppSettings.NormalizeFit(fit);
                _player.StretchMode = ParseFitMode(_settings.WallpaperFit);
                PreviewMedia.Stretch = _player.StretchMode;
                _settings.Save();
            }
        }

        private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;

            if (SpeedComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag &&
                double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed))
            {
                _settings.PlaybackSpeed = speed;
                _player.PlaybackSpeed = speed;
                PreviewMedia.SpeedRatio = speed;
                _settings.Save();
            }
        }

        private void OnLoopChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _settings.LoopPlayback = LoopToggle.IsChecked == true;
            _player.LoopPlayback = _settings.LoopPlayback;
            _settings.Save();
        }

        private void OnStartMinimizedChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _settings.StartMinimized = StartMinimizedToggle.IsChecked == true;
            _settings.Save();
#if !INTONACO_STORE
            if (StartupManager.IsRegistered)
                StartupManager.Register(_settings.StartMinimized);
#endif
        }

        private void OnAutoPlayChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            _settings.AutoPlay = AutoPlayToggle.IsChecked == true;
            _settings.Save();
        }

        private static void SelectComboByTag(ComboBox comboBox, string expectedTag)
        {
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (item.Tag is string tag &&
                    string.Equals(tag, expectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }
        }

        private void SelectSpeedCombo(double speed)
        {
            ComboBoxItem? closest = null;
            double closestDistance = double.MaxValue;

            foreach (ComboBoxItem item in SpeedComboBox.Items)
            {
                if (item.Tag is string tag &&
                    double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double candidate))
                {
                    double distance = Math.Abs(candidate - speed);
                    if (distance < closestDistance)
                    {
                        closestDistance = distance;
                        closest = item;
                    }
                }
            }

            if (closest != null)
                SpeedComboBox.SelectedItem = closest;
        }

        private static Stretch ParseFitMode(string? fit)
        {
            return AppSettings.NormalizeFit(fit) switch
            {
                "Fit" => Stretch.Uniform,
                "Stretch" => Stretch.UniformToFill, // Legacy only: never distort.
                _ => Stretch.UniformToFill
            };
        }

        #endregion

        #region Event Handlers

        private void OnPlayerStateChanged(WallpaperPlayer.PlaybackState state)
        {
            // Resource lifecycle changes happen immediately on the UI thread;
            // only the status/control refresh below is queued.
            if (Dispatcher.CheckAccess())
            {
                RefreshPreviewForCurrentState();
                UpdateBackgroundTimers();
                UpdateSeekTimer();
                // Pausing keeps the native media surface alive. A full GC
                // cannot release that decoder and would stall the UI thread.
            }

            Dispatcher.BeginInvoke(() =>
            {
                switch (state)
                {
                    case WallpaperPlayer.PlaybackState.Playing:
                        PlayPauseButton.Content = "\uE769"; // Pause icon
                        PlayPauseButton.ToolTip = "Pause";
                        PlayPauseButton.IsEnabled = true;
                        StopButton.IsEnabled = true;
                        StatusText.Text = "Playing";
                        StatusText.Foreground = FindResource("SuccessBrush") as System.Windows.Media.Brush;
                        TrayPlayPauseItem.Header = "Pause";
                        TrayIcon.ToolTipText = $"Intonaco — Playing";
                        break;

                    case WallpaperPlayer.PlaybackState.Paused:
                        PlayPauseButton.Content = "\uE768"; // Play icon
                        PlayPauseButton.ToolTip = "Resume";
                        PlayPauseButton.IsEnabled = true;
                        StopButton.IsEnabled = true;
                        StatusText.Text = _pausedByFullscreen ? "Paused (fullscreen)" :
                                         _pausedByForeground ? "Paused (focus)" :
                                         _pausedBySystem ? "Paused (system)" : "Paused";
                        StatusText.Foreground = FindResource("TextSecondaryBrush") as System.Windows.Media.Brush;
                        TrayPlayPauseItem.Header = "Resume";
                        TrayIcon.ToolTipText = "Intonaco — Paused";
                        break;

                    case WallpaperPlayer.PlaybackState.Stopped:
                        PlayPauseButton.Content = "\uE768"; // Play icon
                        PlayPauseButton.ToolTip = "Play";
                        PlayPauseButton.IsEnabled = !string.IsNullOrEmpty(_settings.LastWallpaperPath);
                        StopButton.IsEnabled = false;
                        StatusText.Text = "Stopped";
                        StatusText.Foreground = FindResource("TextSecondaryBrush") as System.Windows.Media.Brush;
                        TrayPlayPauseItem.Header = "Play";
                        TrayIcon.ToolTipText = "Intonaco";
                        // Keep the last file name visible: ApplyWallpaper stops the
                        // previous video first, and blanking the name here race-wins
                        // over UpdateFileInfo for the newly applied wallpaper.
                        break;

                    case WallpaperPlayer.PlaybackState.Error:
                        PlayPauseButton.Content = "\uE768";
                        PlayPauseButton.IsEnabled = !string.IsNullOrEmpty(_settings.LastWallpaperPath);
                        StopButton.IsEnabled = false;
                        StatusText.Text = "Error";
                        StatusText.Foreground = FindResource("ErrorBrush") as System.Windows.Media.Brush;
                        TrayIcon.ToolTipText = "Intonaco — Error";
                        break;
                }

                UpdateMuteButton();
                UpdateDesktopStatus(state);
                RefreshPreviewForCurrentState();
                UpdateBackgroundTimers();
            });
        }

        private void UpdateDesktopStatus(WallpaperPlayer.PlaybackState state)
        {
            string text;
            string previewText;
            string dotBrushKey;

            switch (state)
            {
                case WallpaperPlayer.PlaybackState.Playing:
                    text = "Playing on desktop";
                    previewText = "On desktop";
                    dotBrushKey = "SuccessBrush";
                    break;
                case WallpaperPlayer.PlaybackState.Paused:
                    text = "Paused";
                    previewText = "Paused";
                    dotBrushKey = "TextSecondaryBrush";
                    break;
                case WallpaperPlayer.PlaybackState.Error:
                    text = "Needs attention";
                    previewText = "Error";
                    dotBrushKey = "ErrorBrush";
                    break;
                default:
                    bool hasWallpaper = !string.IsNullOrEmpty(_settings.LastWallpaperPath) &&
                                        File.Exists(_settings.LastWallpaperPath);
                    text = hasWallpaper ? "Ready" : "No wallpaper";
                    previewText = hasWallpaper ? "Ready" : "No video";
                    dotBrushKey = "TextSecondaryBrush";
                    break;
            }

            DesktopStatusText.Text = text;
            PreviewStatusText.Text = previewText;
            DesktopStatusDot.Fill = FindResource(dotBrushKey) as Brush;
        }

        private void OnPlayerError(string message)
        {
            if (Dispatcher.CheckAccess())
                HandlePlayerError(message);
            else
                Dispatcher.BeginInvoke(() => HandlePlayerError(message));
        }

        private void HandlePlayerError(string message)
        {
            if (_isExiting) return;

            string detail;
            if (_player is MpvWallpaperPlayer)
            {
                detail = "MPV could not play this video. Verify your optional MPV " +
                    "installation and its supported codecs. Details: " + message;
            }
            else
            {
                detail = NativeMediaEngine.IsLikelyDecoderFailure(message)
                    ? NativeMediaEngine.CodecHelp +
                        " You can also select the optional MPV backend in Playback settings. " +
                        "Details: " + message
                    : message;
            }

            NotifyPlaybackError(detail);
            // If this video cannot be decoded, restore the previous source
            // rather than leaving an empty or broken desktop.
            RestoreLastWorkingWallpaper();
        }

        private void RestoreLastWorkingWallpaper()
        {
            string? previous = _rollbackOriginalPath;
            bool paused = _rollbackWasPaused;
            TimeSpan position = _rollbackPosition;
            _rollbackOriginalPath = null;
            if (string.IsNullOrEmpty(previous) || !File.Exists(previous))
                return;

            DebugLog.Write("Restoring the previous working wallpaper.");
            ApplyWallpaper(previous, startPaused: paused, pausedPosition: position);
        }

        /// <summary>
        /// Surfaces a playback failure everywhere the user might be looking:
        /// the footer, a tray balloon (the app normally runs hidden), and a
        /// dialog when the main window is already open. Unsupported codecs are
        /// the common case, so the wording always states what to do instead of
        /// leaving a silent black desktop behind.
        /// </summary>
        private void NotifyPlaybackError(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || _isExiting)
                return;

            ShowError(message);
            DebugLog.Write($"Playback error shown to user: {message}");

            try
            {
                TrayIcon.ShowBalloonTip(
                    "Intonaco",
                    message,
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning);
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Tray notification failed: {ex.Message}");
            }

            if (IsVisible && WindowState != WindowState.Minimized)
            {
                MessageBox.Show(this, message, "Intonaco — Video error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnMediaOpened(TimeSpan duration)
        {
            // Decoder events are posted asynchronously. If the owner changes
            // wallpaper before this callback reaches WPF, an old MediaOpened
            // must not mark the *new* source as ready or trigger a second
            // thumbnail extraction while the new GPU session is starting.
            int requestVersion = _wallpaperRequestVersion;
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isApplied || requestVersion != _wallpaperRequestVersion)
                    return;

                // MediaElement can misinterpret broken MP4 pixel-aspect data:
                // e.g. report 232x2160 for a genuine 3840x2160 video. Never
                // stretch such a broken decoder surface across the desktop.
                if (_sourceWidth > 0 && _sourceHeight > 0 &&
                    _player.NaturalVideoWidth > 0 && _player.NaturalVideoHeight > 0 &&
                    // Reject wildly incorrect decoder dimensions without blocking
                    // legitimate non-square-pixel/anamorphic video (such as
                    // 1440x1080 video displayed at 1920x1080).
                    (_player.NaturalVideoWidth < _sourceWidth / 2 ||
                     _player.NaturalVideoWidth > _sourceWidth * 2 ||
                     _player.NaturalVideoHeight < _sourceHeight / 2 ||
                     _player.NaturalVideoHeight > _sourceHeight * 2))
                {
                    string message =
                        "Windows reported incorrect video dimensions (" +
                        _player.NaturalVideoWidth + "×" + _player.NaturalVideoHeight +
                        " instead of " + _sourceWidth + "×" + _sourceHeight +
                        "). Playback was stopped to avoid a distorted wallpaper. " +
                        "Try a standard H.264 MP4 with square pixels.";
                    DebugLog.Write("Decoder geometry rejected: " + message);
                    StopWallpaper();
                    NotifyPlaybackError(message);
                    return;
                }

                string info = _fileInfoBase;
                if (_player.NaturalVideoWidth > 0 && _player.NaturalVideoHeight > 0)
                    info += $" • {_player.NaturalVideoWidth}×{_player.NaturalVideoHeight}";
                if (duration > TimeSpan.Zero)
                    info += $" • {FormatDuration(duration)}";

                _lastVideoOpened = true;
                _rollbackOriginalPath = null;
                FileInfoText.Text = info;
                RefreshRecentWallpapers();
                RefreshSeekProgress();

                // Only after native playback opens may the optional Shell
                // thumbnail provider start. It must not overlap decoder
                // teardown and the following wallpaper's startup.
                if (_player is GpuVideoWallpaperPlayer &&
                    IsPreviewWindowVisible && _previewFilePath != null)
                    ShowStaticPreview();
            });
        }

        private void OnFullscreenChanged(bool isFullscreen)
        {
            Dispatcher.BeginInvoke(() =>
            {
                bool isPlaying = _player.CurrentState == WallpaperPlayer.PlaybackState.Playing;
                if (isFullscreen &&
                    PlaybackFocusPolicy.CanAutoPause(
                        _pausedByUser, _settings.IsAutoPauseEnabled, isPlaying))
                {
                    DebugLog.Write("Auto-pause: fullscreen app detected");
                    _pausedByFullscreen = true;
                    _player.Pause();
                }
                else if (!isFullscreen && _pausedByFullscreen)
                {
                    _pausedByFullscreen = false;
                    if (PlaybackFocusPolicy.CanAutoResume(
                            _pausedByUser, !_pausedBySystem && !_pausedByForeground))
                    {
                        DebugLog.Write("Auto-resume: fullscreen app left");
                        _player.Resume();
                    }
                }
            });
        }

        private void OnForegroundChanged(bool isDesktopForeground)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ForegroundDetector.IsForegroundDesktop(out string proc);
                bool autoPauseEnabled = _settings.IsAutoPauseEnabled;
                bool isPlaying = _player.CurrentState == WallpaperPlayer.PlaybackState.Playing;

                if (!isDesktopForeground &&
                    PlaybackFocusPolicy.CanAutoPause(_pausedByUser, autoPauseEnabled, isPlaying))
                {
                    DebugLog.Write($"Auto-pause: foreground is '{proc}' (not desktop)");
                    _pausedByForeground = true;
                    _player.Pause();
                }
                else if (isDesktopForeground && _pausedByForeground)
                {
                    _pausedByForeground = false;
                    if (PlaybackFocusPolicy.CanAutoResume(
                            _pausedByUser, !_pausedBySystem && !_pausedByFullscreen))
                    {
                        DebugLog.Write($"Auto-resume: foreground returned to desktop ('{proc}')");
                        _player.Resume();
                    }
                }
                else if (!autoPauseEnabled && _pausedByForeground)
                {
                    // Mode B: the detector may still deliver one last event.
                    // Drop focus ownership without resuming a manual pause.
                    _pausedByForeground = false;
                }
            });
        }

        private void OnSystemPause()
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_player.CurrentState == WallpaperPlayer.PlaybackState.Playing &&
                    !_pausedByUser)
                {
                    DebugLog.Write("System pause (lock/sleep)");
                    _pausedBySystem = true;
                    _player.Pause();
                }
            });
        }

        private void OnSystemResume()
        {
            Dispatcher.BeginInvoke(() =>
            {
                _pausedBySystem = false;
                if (PlaybackFocusPolicy.CanAutoResume(
                        _pausedByUser,
                        !_pausedByFullscreen && !_pausedByForeground) &&
                    _player.CurrentState == WallpaperPlayer.PlaybackState.Paused)
                {
                    DebugLog.Write("System resume (unlock/wake)");
                    // Small delay after wake to let display stabilize
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        // Re-check ownership after the delay: a manual
                        // pause made in the meantime must never be resumed.
                        if (_isApplied && !_pausedByUser &&
                            _player.CurrentState == WallpaperPlayer.PlaybackState.Paused)
                        {
                            EnsureAttached();
                            _player.Resume();
                        }
                    };
                    timer.Start();
                }
            });
        }

        private void OnDisplayChanged()
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_isApplied)
                {
                    _desktop.ResizeToDesktop();
                    _playerWindow?.UpdateLayout();
                    // The media element is still the rendering surface even
                    // while paused; WPF re-fits it on layout automatically.
                }
            });
        }

        /// <summary>
        /// Reattaches the player window to the (possibly recreated) WorkerW layer.
        /// When Explorer restarts, the old WorkerW — and any child window parented
        /// to it — is destroyed, so a plain reattach can fail; in that case the
        /// wallpaper is re-applied from scratch with a fresh player window.
        /// </summary>
        private void EnsureAttached()
        {
            if (_desktop.Reattach())
                return;

            string? path = _activeOriginalFile ?? _settings.LastWallpaperPath;
            var state = _player.CurrentState;
            TimeSpan position = _player.Position;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                ApplyWallpaper(
                    path,
                    startPaused: state == WallpaperPlayer.PlaybackState.Paused,
                    pausedPosition: position);
            }
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;

            if (ThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string theme)
            {
                _settings.Theme = theme;
                _settings.Save();
                (Application.Current as App)?.ApplyTheme(theme);
                UpdateTitleBarTheme();
                UpdateDesktopStatus(_player.CurrentState);
            }
        }

        #endregion

        // Discovery remains local and offline. A copy action never redirects
        // the user, fetches a website, or launches another application.
        private void OnToggleWallpaperSources(object sender, RoutedEventArgs e)
        {
            bool expanding = WallpaperSourcesPanel.Visibility != Visibility.Visible;
            WallpaperSourcesPanel.Visibility = expanding
                ? Visibility.Visible : Visibility.Collapsed;
            WallpaperSourcesChevron.Text = expanding ? "\uE70E" : "\uE70D";
            if (!expanding)
                WallpaperSourceCopyStatus.Text = string.Empty;
        }

        private void OnCopyWallpaperSource(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string url } ||
                !WallpaperSources.IsApproved(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
            {
                return;
            }

            try
            {
                Clipboard.SetText(url, TextDataFormat.UnicodeText);
                WallpaperSourceCopyStatus.Text =
                    "Copied " + parsed.Host + " to clipboard";
            }
            catch (ExternalException ex)
            {
                WallpaperSourceCopyStatus.Text = "Clipboard is unavailable. Try again.";
                DebugLog.Write("Wallpaper source copy failed: " + ex.Message);
            }
        }

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            new AboutWindow { Owner = this }.ShowDialog();
        }

        #region Window Management

        private void OnWindowStateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && !_isExiting)
                HideToTray();
        }

        private void HideToTray()
        {
            FlushSettingsSave();
            ReleasePreviewPipeline();
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;

            // Reset Minimized only AFTER hiding. A lingering minimized HWND
            // forces a second click on the desktop shortcut before it paints.
            Hide();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            ShowInTaskbar = false;
        }

         private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            FlushSettingsSave();

            if (!_isExiting)
            {
                // Close means tray. Avoid sending the HWND through a
                // visible minimized state that can break the first restore.
                e.Cancel = true;
                HideToTray();
                return;
            }

            // Actual shutdown
            _preparationCancellation?.Cancel();
            ++_wallpaperRequestVersion;
            _placementTimer?.Stop();
            _settingsSaveTimer?.Stop();
            _seekUpdateTimer?.Stop();
            _seekCommitTimer?.Stop();
            _fullscreenDetector.Dispose();
            _foregroundDetector.Dispose();
            _systemEvents.Dispose();
            StopWallpaper();
            _player.Dispose();
            _desktop.Dispose();
            TrayIcon.Dispose();
        }

        /// <summary>Called when a second shortcut launch asks this instance to reopen.</summary>
        public void RestoreFromSecondaryLaunch()
        {
            if (_isExiting)
                return;

            if (!_firstContentRendered)
            {
                // A shortcut invoked while sign-in is still rendering should
                // cancel silent startup and show the fully painted controls.
                _pendingWindowRestore = true;
                return;
            }

            // Input priority processes a deliberate user launch promptly
            // instead of waiting behind background wallpaper maintenance.
            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                (Action)ShowMainWindow);
        }

        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private void ShowMainWindow()
        {
            if (_isExiting)
                return;

            if (!_firstContentRendered)
            {
                _pendingWindowRestore = true;
                return;
            }

            ShowActivated = true;
            ShowInTaskbar = true;
            Opacity = 1;
            WindowState = WindowState.Normal;
            if (!IsVisible)
                Show();

            // The secondary process is foreground-eligible from a desktop
            // shortcut. Restore the native HWND as well as the WPF state,
            // otherwise the first activation can remain behind the taskbar.
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ShowWindowAsync(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
            }
            Activate();
            Focus();

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                if (_isExiting || !IsVisible || WindowState == WindowState.Minimized)
                    return;
                InvalidateMeasure();
                InvalidateVisual();
                UpdateLayout();
                RefreshPreviewForCurrentState();
                UpdateSeekTimer();
            }));
        }

        #endregion

        #region System Tray

        private void OnTrayShow(object sender, RoutedEventArgs e) => ShowMainWindow();

        private void OnTrayPlayPause(object sender, RoutedEventArgs e) =>
            OnPlayPause(sender, e);

        private void OnTrayToggleMute(object sender, RoutedEventArgs e) =>
            OnToggleMute(sender, e);

        /// <summary>
        /// Tray "Choose video…" — the file dialog needs an owner window, so the
        /// main window is restored first when Intonaco is sitting in the tray.
        /// </summary>
        private void OnTrayChooseVideo(object sender, RoutedEventArgs e)
        {
            if (!IsVisible || WindowState == WindowState.Minimized)
                ShowMainWindow();

            OnChooseFile(sender, e);
        }

        /// <summary>Tray "Settings" simply brings the settings window forward.</summary>
        private void OnTraySettings(object sender, RoutedEventArgs e) => ShowMainWindow();

        private void OnTrayExit(object sender, RoutedEventArgs e)
        {
            _isExiting = true;
            Close();
            Application.Current.Shutdown();
        }

        #endregion

        #region UI Helpers

        private void UpdateMuteButton()
        {
            bool effectivelyMuted = _player.IsMuted || _player.Volume <= 0.001;
            if (effectivelyMuted)
            {
                MuteButton.Content = "\uE74F"; // Muted icon
                MuteButton.ToolTip = "Unmute";
                TrayMuteItem.Header = "Unmute";
            }
            else
            {
                MuteButton.Content = "\uE767"; // Speaker icon
                MuteButton.ToolTip = "Mute";
                TrayMuteItem.Header = "Mute";
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;

            // Auto-clear after 10 seconds
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (ErrorText.Text == message)
                    ErrorText.Text = "";
            };
            timer.Start();
        }

        private void ClearError()
        {
            ErrorText.Text = "";
        }

        #endregion
    }
}
