using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Intonaco.Core
{
    public sealed class AppSettings
    {
        internal const int MaxRecentWallpapers = 6;

        /// <summary>Mode A (default): stop animation updates when focus is lost.</summary>
        public const string PauseWhenUnfocusedMode = "PauseWhenUnfocused";

        /// <summary>Mode B: keep the background animating when focus is lost.</summary>
        public const string ContinueWhenUnfocusedMode = "ContinueWhenUnfocused";

        public string? LastWallpaperPath { get; set; }
        public List<string> RecentWallpaperPaths { get; set; } = new List<string>();
        public bool IsMuted { get; set; } = true;
        public double Volume { get; set; } = 0.0;
        public int TargetFps { get; set; } = 30;
        // Native uses a Windows in-process player (no separate .exe).
        // Windows remains a compatibility fallback for older driver stacks.
        public string PlaybackBackend { get; set; } = "Native"; // "Native", "Windows" or "MPV"
        // Optional user-selected mpv.exe: Intonaco never bundles or downloads it.
        public string? MpvExecutablePath { get; set; }
        public string WallpaperFit { get; set; } = "Fill"; // "Fill", "Fit", "Stretch"
        public double PlaybackSpeed { get; set; } = 1.0;
        public bool LoopPlayback { get; set; } = true;

        /// <summary>Legacy fullscreen-only detection; migrated to PauseWhenUnfocused.</summary>
        public bool PauseOnFullscreen { get; set; } = true;

        /// <summary>
        /// When true, the wallpaper auto-pauses whenever any non-desktop window is
        /// in the foreground (Discord, browsers, games, etc.) and resumes when the
        /// desktop is visible again. This matches Lively Wallpaper behaviour and
        /// saves significant CPU/GPU. Enabled by default.
        /// </summary>
        public bool PauseWhenUnfocused { get; set; } = true;

        /// <summary>
        /// Dedicated user choice for background animation when the app is not
        /// focused. Persisted in settings.json and restored on restart:
        /// <see cref="PauseWhenUnfocusedMode"/> (default) or
        /// <see cref="ContinueWhenUnfocusedMode"/>.
        /// </summary>
        public string BackgroundAnimationMode { get; set; } = PauseWhenUnfocusedMode;

        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public string Theme { get; set; } = "System"; // "Light", "Dark", "System"
        public bool AutoPlay { get; set; } = true;

        [JsonIgnore]
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Intonaco");

        [JsonIgnore]
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

        [JsonIgnore]
        public static string SettingsDirPath => SettingsDir;

        [JsonIgnore]
        public static string SettingsFilePath => SettingsFile;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Canonicalizes a (possibly hand-edited) mode value. Anything that is
        /// not an explicit Mode B request falls back to the safe default.
        /// </summary>
        internal static string NormalizeBackgroundAnimationMode(string? mode)
            => string.Equals(mode, ContinueWhenUnfocusedMode, StringComparison.OrdinalIgnoreCase)
                ? ContinueWhenUnfocusedMode
                : PauseWhenUnfocusedMode;

        internal static string NormalizeBackend(string? backend) =>
            string.Equals(backend, "GPU", StringComparison.OrdinalIgnoreCase)
                ? "GPU" :
            string.Equals(backend, "MPV", StringComparison.OrdinalIgnoreCase)
                ? "MPV" :
            string.Equals(backend, "Windows", StringComparison.OrdinalIgnoreCase)
                ? "Windows" : "Native";

        internal static string NormalizeFit(string? fit)
        {
            if (string.Equals(fit, "Fit", StringComparison.OrdinalIgnoreCase))
                return "Fit";
            // Legacy Stretch can warp a video on monitors with a different
            // aspect ratio. Migrate it to aspect-preserving Cover/Fill.
            if (string.Equals(fit, "Stretch", StringComparison.OrdinalIgnoreCase))
                return "Fill";
            return "Fill";
        }

        internal void Normalize()
        {
            Volume = double.IsNaN(Volume) || double.IsInfinity(Volume)
                ? 0.0
                : Math.Clamp(Volume, 0.0, 1.0);
            TargetFps = Math.Clamp(TargetFps, 1, 60);
            PlaybackSpeed = double.IsNaN(PlaybackSpeed) || double.IsInfinity(PlaybackSpeed)
                ? 1.0
                : Math.Clamp(PlaybackSpeed, 0.25, 2.0);
            WallpaperFit = NormalizeFit(WallpaperFit);
            PlaybackBackend = NormalizeBackend(PlaybackBackend);
            MpvExecutablePath = string.IsNullOrWhiteSpace(MpvExecutablePath)
                ? null : MpvExecutablePath.Trim();
            BackgroundAnimationMode = NormalizeBackgroundAnimationMode(BackgroundAnimationMode);

            // The new mode is the single source of truth. Keep the two legacy
            // flags in sync so older builds reading the same settings file
            // see the same choice and IsAutoPauseEnabled cannot disagree.
            PauseWhenUnfocused = PauseAnimationsWhenUnfocused;
            PauseOnFullscreen = PauseAnimationsWhenUnfocused;

            Theme = Theme switch
            {
                "Light" => "Light",
                "Dark" => "Dark",
                _ => "System"
            };

            RecentWallpaperPaths ??= new List<string>();
            RecentWallpaperPaths = RecentWallpaperPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecentWallpapers)
                .ToList();
        }

        public void RememberWallpaper(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return;

            Normalize();
            RecentWallpaperPaths.RemoveAll(path =>
                string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
            RecentWallpaperPaths.Insert(0, filePath);
            if (RecentWallpaperPaths.Count > MaxRecentWallpapers)
            {
                RecentWallpaperPaths.RemoveRange(
                    MaxRecentWallpapers,
                    RecentWallpaperPaths.Count - MaxRecentWallpapers);
            }
            LastWallpaperPath = filePath;
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                    return LoadFromJson(File.ReadAllText(SettingsFile));
            }
            catch
            {
                // Return defaults on any error
            }
            return new AppSettings();
        }

        /// <summary>
        /// Deserializes persisted settings, applies the v1 → v2 migration and
        /// normalizes the result. Kept separate from <see cref="Load"/> so the
        /// migration can be tested without touching the real settings file.
        /// </summary>
        internal static AppSettings LoadFromJson(string json)
        {
            AppSettings? settings;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }
            catch
            {
                // A corrupt or hand-edited file must never stop Intonaco from
                // starting; fall back to defaults.
                return new AppSettings();
            }

            if (settings == null)
                return new AppSettings();

            // v1.x only had "pause for fullscreen apps". A v1 user who turned
            // that off must not silently gain the new always-on foreground
            // auto-pause, so honour the old opt-out unless the new field was
            // written by v2 itself.
            bool hasNewField =
                json.IndexOf("\"pauseWhenUnfocused\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
                json.IndexOf("\"PauseWhenUnfocused\"", StringComparison.Ordinal) >= 0;

            if (!hasNewField && !settings.PauseOnFullscreen)
                settings.PauseWhenUnfocused = false;

            // v1.x/v2.0.x files have no dedicated mode field. Derive it from
            // the preference they did persist, so an existing user who turned
            // auto-pause off is not silently switched back to Mode A.
            bool hasModeField = json.IndexOf(
                "\"backgroundAnimationMode\"", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasModeField)
            {
                settings.BackgroundAnimationMode = settings.PauseWhenUnfocused
                    ? PauseWhenUnfocusedMode
                    : ContinueWhenUnfocusedMode;
            }

            settings.Normalize();
            return settings;
        }

        public void Save()
        {
            try
            {
                Normalize();
                Directory.CreateDirectory(SettingsDir);
                string json = JsonSerializer.Serialize(this, JsonOptions);
                File.WriteAllText(SettingsFile, json);
            }
            catch
            {
                // Settings save failure is non-critical
            }
        }

        /// <summary>
        /// Whether Mode A is selected. Derived from the dedicated
        /// <see cref="BackgroundAnimationMode"/>, never persisted separately.
        /// </summary>
        [JsonIgnore]
        public bool PauseAnimationsWhenUnfocused =>
            NormalizeBackgroundAnimationMode(BackgroundAnimationMode) == PauseWhenUnfocusedMode;

        /// <summary>
        /// Whether focus-driven auto-pause is currently enabled. The legacy
        /// flags are kept in sync by <see cref="Normalize"/>, so this stays
        /// compatible with code (and tests) that reads either of them.
        /// </summary>
        [JsonIgnore]
        public bool IsAutoPauseEnabled => PauseWhenUnfocused || PauseOnFullscreen;
    }
}
