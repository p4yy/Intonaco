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
        private bool _previewOpened;
        private bool _previewPlayRequested;
        private string? _previewFailedPath;
        private bool? _livePreviewOverride;

        // WPF decodes the preview independently at source resolution.
        // Keep the second decoder disabled automatically for 4K content.
        // Users can enable it explicitly for the current wallpaper.
        internal static bool CanAutoPreview(int width, int height) =>
            width > 0 && height > 0 &&
            (long)width * height <= 2560L * 1440L;

        private bool IsLivePreviewEnabled =>
            _livePreviewOverride ?? CanAutoPreview(
                _player.NaturalVideoWidth, _player.NaturalVideoHeight);

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
            if (_settings.PlaybackBackend == "MPV" &&
                !MpvWallpaperPlayer.TryLocateExecutable(out _))
            {
                DebugLog.Write("Saved MPV backend not installed; using Windows player.");
                _settings.PlaybackBackend = "Windows";
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
            VersionText.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "";

            LoadSettings();
            WireEvents();
            InitializeSystemTray();
            UpdateBackendInterface();

            // Handle silent boot: --minimized / -m / StartMinimized setting.
            // StartupUri calls Show() after this constructor returns, so hiding
            // here alone would still flash the window. Suppress activation now
            // and hide at SourceInitialized, which fires while the HWND is
            // being created and before the window is ever presented.
            // A manual click on the app must always open its controls. The
            // saved startup preference is honoured by StartupManager when
            // Windows launches the app at sign-in, not on every manual launch.
            _startMinimized = StartupManager.ShouldStartMinimized();
#if INTONACO_STORE
            _startMinimized |= StoreStartupManager.IsStartupActivation() &&
                               _settings.StartMinimized;
#endif
            if (_startMinimized)
            {
                ShowActivated = false;
                ShowInTaskbar = false;
                // Never put the hidden window into a minimized WPF state.
                // WPF can otherwise re-show the minimized HWND before it is
                // restored and leave the controls unpainted / black.
                SourceInitialized += OnStartMinimizedSourceInitialized;
                DebugLog.Write(
                    "Start minimized: launching silently in the system tray " +
                    $"(StartMinimized={_settings.StartMinimized}, " +
                    $"flag={StartupManager.ShouldStartMinimized()}).");
            }

            // Auto-play last wallpaper — persisted path + settings restore.
            // This covers Resume last wallpaper toggle; the config file
            // stores LastWallpaperPath, volume, fit, fps, etc.
            if (_settings.AutoPlay && !string.IsNullOrEmpty(_settings.LastWallpaperPath) &&
                File.Exists(_settings.LastWallpaperPath))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    DebugLog.Write($"AutoPlay restoring: {_settings.LastWallpaperPath}");
                    ApplyWallpaper(_settings.LastWallpaperPath);
                });
            }
            else if (!string.IsNullOrEmpty(_settings.LastWallpaperPath) && !File.Exists(_settings.LastWallpaperPath))
            {
                DebugLog.Write($"AutoPlay: last wallpaper not found: {_settings.LastWallpaperPath}");
            }
        }

        // WPF dimensions are device-independent units. Clamp only the startup
        // height; resizing/maximizing remains fully supported afterward.
        internal static double CalculateInitialWindowHeight(
            double workAreaHeight, double minHeight) =>
            Math.Max(minHeight, Math.Min(1080, workAreaHeight - 24));

        private static IWallpaperPlayer CreatePlayer(string backend) =>
            AppSettings.NormalizeBackend(backend) == "MPV"
                ? new MpvWallpaperPlayer() : new WallpaperPlayer();

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
            FpsComboBox.IsEnabled = !mpv;
            FpsComboBox.ToolTip = mpv
                ? "MPV uses the source's native frame cadence; the Windows FPS cap does not apply."
                : "Preferred presentation frame rate.";
            PreviewHintText.Text = mpv
                ? "MPV shows the wallpaper on the desktop without a second decoder."
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
                if (_isApplied)
                    _desktop.EnsurePlacement();
            };
            UpdateBackgroundTimers();
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
        // Losing keyboard focus must NEVER close and reopen the video decoder.
        private bool IsPreviewWindowVisible =>
            IsVisible && WindowState != WindowState.Minimized;

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
            _previewFilePath = filePath;
            _previewFailedPath = null;
            _livePreviewOverride = null;
            RefreshPreviewForCurrentState();
        }

        private void OnToggleLivePreview(object sender, RoutedEventArgs e)
        {
            if (_previewFailedPath != null)
            {
                _previewFailedPath = null;
                _livePreviewOverride = true;
            }
            else
            {
                _livePreviewOverride = !IsLivePreviewEnabled;
            }
            RefreshPreviewForCurrentState();
        }

        private void ShowPreviewMessage(string message)
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewMedia.Visibility = Visibility.Collapsed;
            PreviewHintText.Text = message;
            PreviewPlaceholder.Visibility = Visibility.Visible;
        }

        private void UpdatePreviewToggle()
        {
            PreviewToggleButton.Visibility =
                _previewFilePath != null && _player is not MpvWallpaperPlayer
                    ? Visibility.Visible : Visibility.Collapsed;
            PreviewToggleButton.Content = _previewFailedPath != null
                ? "Retry preview"
                : IsLivePreviewEnabled ? "Pause preview" : "Show preview";
        }

        private void ClearPreview()
        {
            _previewFilePath = null;
            _previewFailedPath = null;
            _livePreviewOverride = null;
            _previewNeedsPositionSync = false;
            PreviewToggleButton.Visibility = Visibility.Collapsed;
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
            UpdatePreviewToggle();
            // MPV has its own decoder and cannot share frames with WPF.
            // Never launch a second incompatible player as a faux preview.
            if (_player is MpvWallpaperPlayer)
            {
                ReleasePreviewPipeline();
                ShowPreviewMessage("MPV plays on your desktop. No extra decoder is running.");
                return;
            }

            if (_previewFilePath == null)
            {
                ReleasePreviewPipeline();
                ShowPreviewMessage("Drop a video here or choose a file.");
                return;
            }

            if (!IsPreviewWindowVisible)
            {
                ReleasePreviewPipeline();
                return;
            }

            if (_previewFailedPath != null)
            {
                ReleasePreviewPipeline();
                ShowPreviewMessage("Preview unavailable. Wallpaper playback is independent. Try Retry preview.");
                return;
            }

            // Hold off until the desktop decoder reports real dimensions.
            // Without this guard every 4K file started a costly second decoder
            // before the auto-preview limit could take effect.
            if (_player.NaturalVideoWidth <= 0 || _player.NaturalVideoHeight <= 0)
            {
                ReleasePreviewPipeline();
                ShowPreviewMessage("Preparing wallpaper on desktop…");
                return;
            }

            if (!IsLivePreviewEnabled)
            {
                ReleasePreviewPipeline();
                ShowPreviewMessage("Live preview is off to keep wallpaper playback smooth.");
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
                    ShowPreviewMessage("Wallpaper is stopped.");
                    break;
            }
        }

        private void StartPreview()
        {
            if (_previewFilePath == null || !IsPreviewWindowVisible ||
                !IsLivePreviewEnabled ||
                _player.CurrentState != WallpaperPlayer.PlaybackState.Playing)
                return;

            try
            {
                bool sourceChanged = PreviewMedia.Source == null ||
                    !string.Equals(PreviewMedia.Source.LocalPath,
                        _previewFilePath, StringComparison.OrdinalIgnoreCase);

                if (sourceChanged)
                {
                    ReleasePreviewPipeline();
                    PreviewMedia.Source = new Uri(_previewFilePath, UriKind.Absolute);
                    _previewNeedsPositionSync = true;
                    _previewOpened = false;
                    ShowPreviewMessage("Loading preview…");
                }

                PreviewMedia.Stretch = _player.StretchMode;
                PreviewMedia.SpeedRatio = _player.PlaybackSpeed;
                ApplyPreviewFrameRateCap(_settings.TargetFps);
                PreviewMedia.IsMuted = true;
                // Do not restart an already-running decoder on every activation,
                // placement, slider or state refresh.
                if (!_previewPlayRequested)
                {
                    PreviewMedia.Play();
                    _previewPlayRequested = true;
                }

                if (_previewOpened)
                {
                    PreviewMedia.Visibility = Visibility.Visible;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("Preview start failed: " + ex.Message);
                _previewFailedPath = _previewFilePath;
                ReleasePreviewPipeline();
                ShowPreviewMessage("Preview could not open this video. The desktop player is separate.");
                UpdatePreviewToggle();
            }
        }

        private void ShowPausedPreview()
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;

            if (PreviewMedia.Source != null)
            {
                try
                {
                    PreviewMedia.Pause();
                    _previewPlayRequested = false;
                    PreviewMedia.Visibility = _previewOpened
                        ? Visibility.Visible : Visibility.Collapsed;
                    PreviewPlaceholder.Visibility = _previewOpened
                        ? Visibility.Collapsed : Visibility.Visible;
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Paused preview failed: " + ex.Message);
                    StartPausedPreview();
                }
                return;
            }

            StartPausedPreview();
        }

        private void StartPausedPreview()
        {
            if (_previewFilePath == null || !IsPreviewWindowVisible ||
                !IsLivePreviewEnabled)
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
                // Prime the native media pipeline; MediaOpened seeks and
                // immediately pauses before exposing the preview surface.
                PreviewMedia.Play();
                _previewPlayRequested = true;
                ShowPreviewMessage("Preparing paused preview…");
            }
            catch (Exception ex)
            {
                DebugLog.Write("Paused preview open failed: " + ex.Message);
                _previewFailedPath = _previewFilePath;
                ReleasePreviewPipeline();
                ShowPreviewMessage("Paused preview unavailable.");
                UpdatePreviewToggle();
            }
        }

        private void ReleasePreviewPipeline()
        {
            // Calling Stop/Close on every focus event was expensive and could
            // leave the MediaElement black during the next activation.
            if (PreviewMedia.Source == null && !_previewPlayRequested &&
                !_previewOpened)
            {
                PreviewMedia.Visibility = Visibility.Collapsed;
                return;
            }
            try
            {
                PreviewMedia.Stop();
                PreviewMedia.Source = null;
                PreviewMedia.Close();
            }
            catch (Exception ex)
            {
                DebugLog.Write("Preview cleanup: " + ex.Message);
            }
            _previewOpened = false;
            _previewPlayRequested = false;
            _previewNeedsPositionSync = false;
            PreviewMedia.Visibility = Visibility.Collapsed;
        }

        private void OnPreviewMediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            DebugLog.Write("Preview decoder failed: " +
                (e.ErrorException?.Message ?? "unknown media error"));
            _previewFailedPath = _previewFilePath;
            ReleasePreviewPipeline();
            ShowPreviewMessage("This file cannot be previewed by Windows. Desktop playback may still work.");
            UpdatePreviewToggle();
        }

        private void OnPreviewMediaOpened(object sender, RoutedEventArgs e)
        {
            // MediaOpened fires before the first frame; do not show a blank
            // surface if Windows reports no decoded video track.
            if (_previewFilePath == null ||
                PreviewMedia.NaturalVideoWidth <= 0 ||
                PreviewMedia.NaturalVideoHeight <= 0)
            {
                _previewFailedPath = _previewFilePath;
                ReleasePreviewPipeline();
                ShowPreviewMessage("No playable video track for this preview.");
                UpdatePreviewToggle();
                return;
            }

            _previewOpened = true;
            if (_player.CurrentState == WallpaperPlayer.PlaybackState.Paused)
            {
                try
                {
                    if (_previewNeedsPositionSync &&
                        _player.Position > TimeSpan.FromMilliseconds(500))
                        PreviewMedia.Position = _player.Position;
                    PreviewMedia.Pause();
                    _previewPlayRequested = false;
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Preview paused seek failed: " + ex.Message);
                }

                _previewNeedsPositionSync = false;
            }
            else if (_previewNeedsPositionSync &&
                     _player.CurrentState == WallpaperPlayer.PlaybackState.Playing)
            {
                _previewNeedsPositionSync = false;
                try
                {
                    TimeSpan desktopPosition = _player.Position;
                    if (desktopPosition > TimeSpan.FromMilliseconds(500) &&
                        (PreviewMedia.Position - desktopPosition).Duration() >
                            TimeSpan.FromMilliseconds(500))
                        PreviewMedia.Position = desktopPosition;
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Preview position synchronization failed: " +
                        ex.Message);
                }
            }
            PreviewMedia.Visibility = IsPreviewWindowVisible
                ? Visibility.Visible : Visibility.Collapsed;
            PreviewPlaceholder.Visibility = IsPreviewWindowVisible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnPreviewMediaEnded(object sender, RoutedEventArgs e)
        {
            if (!IsPreviewWindowVisible ||
                _player.CurrentState != WallpaperPlayer.PlaybackState.Playing ||
                !_settings.LoopPlayback)
                return;

            try
            {
                PreviewMedia.Position = TimeSpan.Zero;
                PreviewMedia.Play();
                _previewPlayRequested = true;
            }
            catch (Exception ex)
            {
                DebugLog.Write("Preview loop failed: " + ex.Message);
            }
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
                ApplyPreviewFrameRateCap(fps);
                _settings.Save();
                DebugLog.Write(
                    $"Frame rate changed to {fps} fps; desktop cap " +
                    $"{(fps >= 60 ? "cleared" : fps + " fps")} and preview cap re-applied.");
            }
        }

        /// <summary>
        /// Mirrors the desktop player's frame-rate cap onto the in-app preview
        /// so the two never present at different rates. 60 fps clears the cap.
        /// </summary>
        private void ApplyPreviewFrameRateCap(int fps)
        {
            try
            {
                if (fps >= 60)
                    PreviewMedia.ClearValue(
                        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty);
                else
                    PreviewMedia.SetValue(
                        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty,
                        Math.Clamp(fps, 1, 60));
            }
            catch (Exception ex)
            {
                DebugLog.Write($"Preview frame-rate cap failed: {ex.Message}");
            }
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
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isApplied) return;

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
                // Dimensions are now known: enable one preview decoder only
                // for manageable sources, not for every 4K wallpaper.
                RefreshPreviewForCurrentState();
                FileInfoText.Text = info;
                RefreshRecentWallpapers();
                RefreshSeekProgress();
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
            if (WindowState == WindowState.Minimized)
            {
                FlushSettingsSave();
                ReleasePreviewPipeline();
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
                Hide();
                ShowInTaskbar = false;
            }
        }

        /// <summary>
        /// Silent-boot completion: the HWND now exists but has not been
        /// presented, so hiding here starts Intonaco straight in the tray with
        /// the wallpaper already playing and no window flash.
        /// </summary>
        private void OnStartMinimizedSourceInitialized(object? sender, EventArgs e)
        {
            SourceInitialized -= OnStartMinimizedSourceInitialized;
            _startMinimized = false;
            // Hide before the first paint without storing WindowState.Minimized.
            // Explicit restore must start from a clean WindowState.Normal.
            ShowInTaskbar = false;
            Hide();
            DebugLog.Write("Start minimized: window suppressed, running from the tray.");
        }

        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            FlushSettingsSave();

            if (!_isExiting)
            {
                // Minimize to tray instead of closing
                e.Cancel = true;
                WindowState = WindowState.Minimized;
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

            // Queue an existing-instance request behind layout and any
            // in-flight minimize/close events. This handles desktop shortcut
            // and Start menu launches the same way as the working tray action.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)ShowMainWindow);
        }

        private void ShowMainWindow()
        {
            if (_isExiting)
                return;

            // IMPORTANT: WPF must not Show() while WindowState is Minimized.
            // Showing first can re-hide the HWND through StateChanged and leave
            // a black / inactive window. Restore the state before showing.
            ShowActivated = true;
            WindowState = WindowState.Normal;
            ShowInTaskbar = true;
            Show();
            Activate();

            // A minimized startup window or a second-instance signal can
            // arrive before first layout. Refresh after the restored HWND has
            // completed layout and is ready to paint.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                if (_isExiting || !IsVisible || WindowState == WindowState.Minimized)
                    return;
                Activate();
                Focus();
                InvalidateVisual();
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
