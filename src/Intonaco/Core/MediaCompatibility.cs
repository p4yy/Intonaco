using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Intonaco.Core
{
    /// <summary>
    /// Compatibility conversion happens on demand, never during playback.
    /// Native Media Foundation remains the normal lightweight video engine.
    /// An optional user-installed FFmpeg executable handles formats that the
    /// Windows decoder cannot open. Nothing is downloaded or bundled.
    /// </summary>
    internal static class MediaCompatibility
    {
        private static readonly SemaphoreSlim ConversionGate = new(1, 1);
        private const long MaxConvertedFileBytes = 4L * 1024 * 1024 * 1024;
        private const long MaxTotalCacheBytes = 8L * 1024 * 1024 * 1024;

        internal const string InstallationHint =
            "To play this format, install FFmpeg from https://ffmpeg.org/download.html " +
            "and add ffmpeg.exe to PATH, or place ffmpeg.exe beside Intonaco.exe. " +
            "Restart Intonaco after installing. Native MP4 playback needs no FFmpeg.";

        /// <summary>Return an existing executable only; never invoke a shell.</summary>
        internal static bool TryFindFfmpeg(out string executable)
        {
            string? configured = Environment.GetEnvironmentVariable("INTONACO_FFMPEG");
            if (IsExecutableFile(configured))
            {
                executable = Path.GetFullPath(configured!);
                return true;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (IsExecutableFile(local))
            {
                executable = local;
                return true;
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string item in path.Split(Path.PathSeparator,
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        string directory = item.Trim().Trim('"');
                        string candidate = Path.Combine(directory, "ffmpeg.exe");
                        if (!IsExecutableFile(candidate)) continue;
                        executable = Path.GetFullPath(candidate);
                        return true;
                    }
                    catch (Exception ex) when (
                        ex is ArgumentException or NotSupportedException
                        or PathTooLongException)
                    {
                        // One broken PATH entry must not prevent discovery.
                    }
                }
            }

            executable = string.Empty;
            return false;
        }

        private static bool IsExecutableFile(string? path)
        {
            try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
            catch { return false; }
        }

        internal static bool IsLikelyDecoderFailure(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            string lower = message.ToLowerInvariant();
            if (lower.Contains("not found") || lower.Contains("access denied") ||
                lower.Contains("permission") || lower.Contains("cannot read"))
                return false;
            return lower.Contains("codec") || lower.Contains("format") ||
                   lower.Contains("decode") || lower.Contains("media") ||
                   lower.Contains("play video") || lower.Contains("source") ||
                   lower.Contains("0xc00d");
        }

        /// <summary>
        /// Prefer a stream-copy remux (lossless video). If remux cannot produce
        /// MP4, encode H.264 at high quality without changing the source file.
        /// A later retry may explicitly request an H.264 transcode when the
        /// remuxed video is not supported by Windows Media Foundation.
        /// </summary>
        internal static async Task<string> ConvertAsync(
            string source, bool forceH264, CancellationToken cancellation = default,
            string? currentlyPlayingPath = null)
        {
            if (!TryFindFfmpeg(out string ffmpeg))
                throw new NotSupportedException(InstallationHint);

            string input = Path.GetFullPath(source);
            if (!MediaFormatProbe.TryValidate(input, out string error))
                throw new IOException(error);

            var sourceInfo = new FileInfo(input);
            string identity = input + "|" + sourceInfo.Length + "|" +
                              sourceInfo.LastWriteTimeUtc.Ticks;
            string fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
            string cacheDir = Path.Combine(
                AppSettings.SettingsDirPath, "compatible-video");

            await ConversionGate.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(cacheDir);
                string? lastError = null;
                foreach (bool encode in forceH264
                             ? new[] { true } : new[] { false, true })
                {
                    cancellation.ThrowIfCancellationRequested();
                    string cached = Path.Combine(
                        cacheDir, fingerprint + (encode ? "-h264.mp4" : "-copy.mp4"));

                    if (File.Exists(cached) && new FileInfo(cached).Length > 1024)
                        return cached;

                    string temporary = Path.Combine(cacheDir,
                        fingerprint + "-" + Guid.NewGuid().ToString("N") + ".mp4");
                    try
                    {
                        string result = await RunFfmpegAsync(
                            ffmpeg, input, temporary, encode, cancellation)
                            .ConfigureAwait(false);
                        if (result.Length != 0)
                        {
                            lastError = result;
                            continue;
                        }

                        if (!File.Exists(temporary) ||
                            new FileInfo(temporary).Length < 1024)
                        {
                            lastError = "FFmpeg did not create a playable video.";
                            continue;
                        }

                        if (new FileInfo(temporary).Length >=
                            MaxConvertedFileBytes - 1024 * 1024)
                        {
                            lastError = "The compatibility cache limit (4 GB) was reached.";
                            continue;
                        }

                        File.Move(temporary, cached, overwrite: true);
                        PruneCache(cacheDir, cached, currentlyPlayingPath);
                        return cached;
                    }
                    finally
                    {
                        try { File.Delete(temporary); } catch { }
                    }
                }

                throw new InvalidOperationException(
                    "FFmpeg could not prepare this video: " +
                    (lastError ?? "unsupported or damaged video stream."));
            }
            finally
            {
                ConversionGate.Release();
            }
        }

        private static void PruneCache(
            string directory, string keep, string? currentlyPlaying)
        {
            // Cache pruning runs only after a completed conversion and on the
            // conversion worker, never on a rendering callback. Never delete
            // the new video or another cached video currently being played.
            try
            {
                var files = new DirectoryInfo(directory)
                    .GetFiles("*.mp4", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToArray();

                long total = files.Sum(file => file.Length);
                foreach (FileInfo file in files.Reverse())
                {
                    if (total <= MaxTotalCacheBytes) break;
                    if (string.Equals(file.FullName, keep, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(file.FullName, currentlyPlaying,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        long length = file.Length;
                        file.Delete();
                        total -= length;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Empty result means success; otherwise a short actionable error.
        private static async Task<string> RunFfmpegAsync(
            string ffmpeg, string input, string output,
            bool encode, CancellationToken cancellation)
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };
            var args = start.ArgumentList;
            args.Add("-hide_banner");
            args.Add("-loglevel");
            args.Add("error");
            args.Add("-nostdin");
            args.Add("-n");
            args.Add("-i");
            args.Add(input);
            args.Add("-map");
            args.Add("0:v:0");
            args.Add("-map");
            args.Add("0:a:0?");
            args.Add("-sn");
            args.Add("-dn");

            if (encode)
            {
                args.Add("-c:v");
                args.Add("libx264");
                args.Add("-preset");
                args.Add("veryfast");
                args.Add("-crf");
                args.Add("18");
                args.Add("-pix_fmt");
                args.Add("yuv420p");
                // Preserve display aspect ratio when the source has
                // non-square pixels by baking the equivalent display width
                // into the H.264 frames, then outputting square pixels.
                // This avoids distorting legitimate anamorphic videos.
                args.Add("-vf");
                args.Add("scale=trunc(iw*sar/2)*2:ih,setsar=1");
            }
            else
            {
                args.Add("-c:v");
                args.Add("copy");
            }

            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("160k");
            args.Add("-movflags");
            args.Add("+faststart");
            args.Add("-fs");
            args.Add(MaxConvertedFileBytes.ToString());
            args.Add("-f");
            args.Add("mp4");
            args.Add(output);

            using var process = new Process { StartInfo = start };
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (!process.Start())
                    return "Unable to launch FFmpeg.";

                Task<string> errorReader = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
                string stderr = await errorReader.ConfigureAwait(false);
                if (process.ExitCode == 0)
                    return string.Empty;

                string detail = stderr.Trim();
                if (detail.Length > 700)
                    detail = detail[^700..];
                return string.IsNullOrEmpty(detail)
                    ? "FFmpeg exited with code " + process.ExitCode + "."
                    : detail;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { }
                throw;
            }
        }
    }
}
