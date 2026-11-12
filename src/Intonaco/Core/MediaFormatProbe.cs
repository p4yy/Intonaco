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
    /// desktop or a silent hang. This probe rejects missing, empty, unreadable
    /// and unrecognized files up front. Container and codec support depend
    /// on Media Foundation and the user's installed Windows decoders.
    ///
    /// Codec-level support (for example HEVC without the HEVC Video Extensions)
    /// still has to be discovered by the decoder; <see cref="WallpaperPlayer"/>
    /// translates those failures into the same friendly wording.
    /// </summary>
    public static class MediaFormatProbe
    {
        // This is a list of containers worth trying with the installed
        // Windows Media Foundation codecs. Recognition is NOT a guarantee
        // that every stream inside the container can be decoded.
        private static readonly string[] VideoExtensions =
        {
            ".mp4", ".m4v", ".mp4v", ".mov", ".qt",
            ".mkv", ".webm", ".avi", ".wmv", ".asf",
            ".mpg", ".mpeg", ".m2ts", ".mts", ".m2t", ".ts",
            ".3gp", ".3g2", ".3gpp", ".vob",
            ".flv", ".f4v", ".ogv", ".ogg", ".divx",
            ".rm", ".rmvb", ".amv", ".nsv", ".mxf",
            ".mpv", ".m2v", ".dat", ".dv", ".nut", ".y4m"
        };

        private static readonly HashSet<string> RecognizedExtensions =
            new HashSet<string>(VideoExtensions, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> UncommonContainers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".flv", ".f4v", ".ogv", ".ogg", ".divx", ".rm", ".rmvb",
                ".amv", ".nsv", ".mxf", ".dv", ".nut", ".y4m", ".dat"
            };

        public static string FileDialogFilter =>
            "Video files|" +
            string.Join(";", Array.ConvertAll(VideoExtensions, ext => "*" + ext)) +
            "|All files|*.*";

        public const string UnsupportedFormatMessage =
            "Unrecognized video file extension. Select a video file.";

        public static bool IsSupportedExtension(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && RecognizedExtensions.Contains(ext);
        }

        public static bool IsUncommonContainer(string? path) =>
            !string.IsNullOrWhiteSpace(path) &&
            UncommonContainers.Contains(Path.GetExtension(path));

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
        /// Convenience wrapper returning null when preflight succeeds.
        /// Actual decoding may still fail for an unsupported codec.
        /// </summary>
        public static string? Validate(string? path)
            => TryValidate(path, out string error) ? null : error;
    }
}
