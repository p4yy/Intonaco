using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Intonaco.Core
{
    /// <summary>
    /// Lightweight, app-owned media preparation and validation.
    ///
    /// Video decoding remains Microsoft's Media Foundation on Windows:
    /// duplicating dozens of decoders in managed code would enlarge the app
    /// and consume more resources without matching proven native decoders.
    /// This layer recognizes file containers, prevents unsafe metadata from
    /// distorting the desktop, and leaves video samples unmodified.
    /// </summary>
    internal static class NativeMediaEngine
    {
        internal enum Container
        {
            Unknown, IsoBmff, Matroska, Avi, Asf, MpegTransport,
            MpegProgram, Ogg, FlashVideo, RealMedia, Gif, WebP
        }

        internal const string CodecHelp =
            "Windows cannot decode this video stream with its installed Media " +
            "Foundation codecs. Try an H.264 MP4 or install a Windows codec " +
            "supported by Media Foundation. Your source file was not changed.";

        /// <summary>
        /// Reads only the initial 512 bytes, without decoding or loading
        /// the media file into memory. This is diagnostic: a recognized
        /// container does not imply the operating system supports its codec.
        /// </summary>
        internal static Container IdentifyContainer(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, bufferSize: 512, FileOptions.SequentialScan);
            Span<byte> prefix = stackalloc byte[512];
            int count = stream.Read(prefix);
            return IdentifyContainer(prefix[..count]);
        }

        internal static Container IdentifyContainer(ReadOnlySpan<byte> header)
        {
            if (header.Length >= 12 && Is(header, 0, "RIFF") &&
                Is(header, 8, "AVI "))
                return Container.Avi;

            if (header.Length >= 12 && Is(header, 0, "RIFF") &&
                Is(header, 8, "WEBP"))
                return Container.WebP;

            if (header.Length >= 8 && (Is(header, 4, "ftyp") ||
                Is(header, 4, "moov") || Is(header, 4, "wide") ||
                Is(header, 4, "mdat")))
                return Container.IsoBmff;

            if (header.Length >= 4 &&
                header[0] == 0x1a && header[1] == 0x45 &&
                header[2] == 0xdf && header[3] == 0xa3)
                return Container.Matroska;

            if (header.Length >= 16 &&
                header[0] == 0x30 && header[1] == 0x26 &&
                header[2] == 0xb2 && header[3] == 0x75 &&
                header[4] == 0x8e && header[5] == 0x66 &&
                header[6] == 0xcf && header[7] == 0x11)
                return Container.Asf;

            if (header.Length >= 4 && Is(header, 0, "OggS"))
                return Container.Ogg;

            if (header.Length >= 4 && Is(header, 0, ".RMF"))
                return Container.RealMedia;

            if (header.Length >= 3 && Is(header, 0, "FLV"))
                return Container.FlashVideo;

            if (header.Length >= 6 &&
                (Is(header, 0, "GIF87a") || Is(header, 0, "GIF89a")))
                return Container.Gif;

            if (header.Length >= 189 && header[0] == 0x47 &&
                header[188] == 0x47)
                return Container.MpegTransport;

            if (header.Length >= 4 &&
                header[0] == 0x00 && header[1] == 0x00 &&
                header[2] == 0x01 && header[3] == 0xba)
                return Container.MpegProgram;

            return Container.Unknown;
        }

        private static bool Is(ReadOnlySpan<byte> bytes, int offset, string ascii)
        {
            if (offset < 0 || bytes.Length - offset < ascii.Length)
                return false;
            for (int i = 0; i < ascii.Length; i++)
            {
                if (bytes[offset + i] != (byte)ascii[i])
                    return false;
            }
            return true;
        }

        internal static bool IsLikelyDecoderFailure(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string lower = message.ToLowerInvariant();
            if (lower.Contains("not found") || lower.Contains("access denied") ||
                lower.Contains("permission") || lower.Contains("cannot read"))
                return false;

            return lower.Contains("codec") ||
                   lower.Contains("format") ||
                   lower.Contains("decode") ||
                   lower.Contains("media") ||
                   lower.Contains("play video") ||
                   lower.Contains("source") ||
                   lower.Contains("0xc00d");
        }

        internal static async Task<PreparedWallpaper> PrepareAsync(
            string path, CancellationToken cancellationToken = default)
        {
            if (!MediaFormatProbe.TryValidate(path, out string reason))
                throw new IOException(reason);

            cancellationToken.ThrowIfCancellationRequested();

            // No per-frame work or external executable. Both lightweight
            // container inspection and optional metadata correction happen
            // away from the WPF dispatcher.
            Container container = await Task.Run(
                () => IdentifyContainer(path), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (container == Container.Gif || container == Container.WebP)
                throw new NotSupportedException(
                    "This file contains an animated image, not a video stream.");

            PreparedWallpaper prepared = await Task.Run(
                () => Mp4AspectSafety.PrepareAsync(path), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            DebugLog.Write("Native media prepared: container=" + container +
                ", source=" + Path.GetFileName(path) +
                ", corrected-aspect=" + prepared.CorrectedAspect);
            return prepared;
        }
    }
}
