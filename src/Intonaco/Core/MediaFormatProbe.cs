using System;
using System.Collections.Generic;
using System.IO;

namespace Intonaco.Core
{
    /// <summary>
    /// Pre-flight validation for wallpaper files.
    ///
    /// Windows Media Foundation reports failures only after it has opened the
    /// file, which is why an unsupported codec used to look like a black
    /// desktop or a silent hang. This probe rejects the cases that can be
    /// decided up front — missing file, empty file, unreadable file, and a
    /// container Media Foundation cannot demux at all — so the user gets an
    /// immediate, actionable message instead.
    ///
    /// Codec-level support (for example HEVC without the HEVC Video Extensions)
    /// still has to be discovered by the decoder; <see cref="WallpaperPlayer"/>
    /// translates those failures into the same friendly wording.
    /// </summary>
    public static class MediaFormatProbe
    {
        // Most of these containers decode natively on compatible Windows
        // installations. Less common formats are accepted and normalized
        // through optional, on-demand FFmpeg compatibility conversion.
        private static readonly string[] VideoExtensions =
        {
            ".mp4", ".m4v", ".mp4v", ".mov", ".qt",
            ".mkv", ".webm", ".avi", ".wmv", ".asf",
            ".mpg", ".mpeg", ".m2ts", ".mts", ".m2t", ".ts",
            ".3gp", ".3g2", ".3gpp",
            ".flv", ".f4v", ".vob", ".ogv", ".ogg", ".divx",
            ".rm", ".rmvb", ".amv", ".nsv", ".mxf", ".mpv",
            ".m2v", ".dat", ".dv", ".nut", ".y4m",
            ".gif", ".apng", ".webp"
        };

        private static readonly HashSet<string> SupportedExtensions =
            new HashSet<string>(VideoExtensions, StringComparer.OrdinalIgnoreCase);

        // These are not reliable native WPF MediaElement sources. We prepare
        // an H.264 MP4 before replacing any already-playing wallpaper.
        private static readonly HashSet<string> CompatibilityOnlyExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".flv", ".f4v", ".vob", ".ogv", ".ogg", ".divx",
                ".rm", ".rmvb", ".amv", ".nsv", ".mxf", ".mpv",
                ".m2v", ".dat", ".dv", ".nut", ".y4m",
                ".gif", ".apng", ".webp"
            };

        public static string FileDialogFilter =>
            "Video and animated files|" +
            string.Join(";", Array.ConvertAll(VideoExtensions, ext => "*" + ext)) +
            "|All files|*.*";

        /// <summary>
        /// A recognized extension is accepted for a playback attempt or
        /// conversion; that is not a promise the installed decoder supports it.
        /// </summary>
        public static bool RequiresCompatibilityConversion(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return CompatibilityOnlyExtensions.Contains(Path.GetExtension(path));
        }

        public const string UnsupportedFormatMessage =
            "This file is not a recognized video or animated image format.";

        public static bool IsSupportedExtension(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext);
        }

        // Kept as an API alias for older callers: these formats need FFmpeg,
        // but are no longer rejected by Intonaco's preflight.
        public static bool IsKnownUnsupportedExtension(string? path) =>
            RequiresCompatibilityConversion(path);

        /// <summary>
        /// Validates a candidate wallpaper file. Returns true when the file is
        /// worth handing to the decoder; otherwise <paramref name="error"/>
        /// receives a user-facing explanation.
        /// </summary>
        public static bool TryValidate(string? path, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "No wallpaper file was selected.";
                return false;
            }

            if (!IsSupportedExtension(path))
            {
                error = UnsupportedFormatMessage +
                        $" ({Path.GetFileName(path)})";
                return false;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(path);
            }
            catch (Exception ex)
            {
                error = $"Cannot read that file: {ex.Message}";
                return false;
            }

            if (!info.Exists)
            {
                error = $"File not found: {Path.GetFileName(path)}";
                return false;
            }

            if (info.Length == 0)
            {
                error = $"That video file is empty: {Path.GetFileName(path)}";
                return false;
            }

            // A file locked by another process (or on a disconnected drive)
            // fails inside Media Foundation with an opaque HRESULT. Probe it
            // here with the sharing mode a wallpaper needs.
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (!stream.CanRead)
                {
                    error = $"That video file cannot be read: {Path.GetFileName(path)}";
                    return false;
                }
            }
            catch (UnauthorizedAccessException)
            {
                error = $"Windows denied access to that video file: {Path.GetFileName(path)}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Cannot open that video file: {ex.Message}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Convenience wrapper returning null when the file is playable.
        /// </summary>
        public static string? Validate(string? path)
            => TryValidate(path, out string error) ? null : error;
    }
}
