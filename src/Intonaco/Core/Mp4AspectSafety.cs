using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Intonaco.Core
{
    internal readonly record struct PreparedWallpaper(
        string PlaybackPath, int SourceWidth, int SourceHeight, bool CorrectedAspect);

    /// <summary>
    /// Some MP4 encoders write enormous, unreduced 'pasp' numerator/denominator
    /// values for virtually square pixels. Windows Media Foundation may
    /// misinterpret those values and report a 3840-pixel-wide video as only a
    /// few hundred pixels wide. WPF then stretches the corrupt decoder surface.
    ///
    /// This helper repairs ONLY the pixel-aspect-ratio metadata in a separate
    /// cached copy; compressed video frames are copied byte-for-byte. The
    /// source file and its quality are never modified. It creates no decoder,
    /// timer, or external process.
    /// </summary>
    internal static class Mp4AspectSafety
    {
        private const int MaxMovieMetadataBytes = 64 * 1024 * 1024;
        private static readonly SemaphoreSlim CacheGate = new(1, 1);

        internal readonly record struct Metadata(
            int Width, int Height, long AspectValuesOffset,
            uint HorizontalSpacing, uint VerticalSpacing)
        {
            public bool NeedsRepair
            {
                get
                {
                    if (AspectValuesOffset <= 0 ||
                        HorizontalSpacing == 0 || VerticalSpacing == 0)
                        return false;

                    double ratio = (double)HorizontalSpacing / VerticalSpacing;
                    return (HorizontalSpacing > 65535 || VerticalSpacing > 65535) &&
                           ratio >= 0.5 && ratio <= 5;
                }
            }
        }

        internal static Metadata Inspect(string path)
        {
            if (!IsMp4Family(path))
                return default;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, bufferSize: 4096, FileOptions.SequentialScan);

            // Walk top-level ISO-BMFF boxes; do not read the potentially huge
            // mdat video payload into memory. Allocate both headers once.
            Span<byte> head = stackalloc byte[8];
            Span<byte> extended = stackalloc byte[8];
            while (stream.Length - stream.Position >= 8)
            {
                long boxStart = stream.Position;
                stream.ReadExactly(head);
                ulong boxSize = BinaryPrimitives.ReadUInt32BigEndian(head[..4]);
                int headerSize = 8;
                if (boxSize == 1)
                {
                    stream.ReadExactly(extended);
                    boxSize = BinaryPrimitives.ReadUInt64BigEndian(extended);
                    headerSize = 16;
                }
                else if (boxSize == 0)
                {
                    boxSize = (ulong)(stream.Length - boxStart);
                }

                if (boxSize < (ulong)headerSize ||
                    boxSize > (ulong)(stream.Length - boxStart))
                    break;

                bool isMovie = head[4] == (byte)'m' && head[5] == (byte)'o' &&
                               head[6] == (byte)'o' && head[7] == (byte)'v';
                if (isMovie)
                {
                    ulong payloadLength = boxSize - (ulong)headerSize;
                    if (payloadLength > MaxMovieMetadataBytes)
                        return default;

                    byte[] movie = new byte[(int)payloadLength];
                    stream.ReadExactly(movie);
                    return InspectMovie(movie, boxStart + headerSize);
                }

                stream.Position = boxStart + (long)boxSize;
            }

            return default;
        }

        private static Metadata InspectMovie(byte[] movie, long movieOffset)
        {
            int width = 0, height = 0;
            long aspectOffset = 0;
            uint horizontal = 0, vertical = 0;

            // The signatures are only accepted at valid MP4 box boundaries:
            // 16-byte pasp boxes and image sample entries with real dimensions.
            // Searching within 'moov' avoids scanning compressed video bytes.
            for (int i = 4; i + 8 <= movie.Length; i++)
            {
                if (movie[i] == (byte)'a' && movie[i + 1] == (byte)'v' &&
                    movie[i + 2] == (byte)'c' && movie[i + 3] == (byte)'1' &&
                    i + 32 <= movie.Length &&
                    BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(i - 4, 4)) >= 86)
                {
                    int candidateWidth =
                        BinaryPrimitives.ReadUInt16BigEndian(movie.AsSpan(i + 28, 2));
                    int candidateHeight =
                        BinaryPrimitives.ReadUInt16BigEndian(movie.AsSpan(i + 30, 2));
                    if (candidateWidth >= 64 && candidateHeight >= 64 &&
                        candidateWidth <= 16384 && candidateHeight <= 16384)
                    {
                        width = candidateWidth;
                        height = candidateHeight;
                    }
                }

                if (movie[i] != (byte)'p' || movie[i + 1] != (byte)'a' ||
                    movie[i + 2] != (byte)'s' || movie[i + 3] != (byte)'p' ||
                    i + 12 > movie.Length ||
                    BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(i - 4, 4)) != 16)
                    continue;

                uint h = BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(i + 4, 4));
                uint v = BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(i + 8, 4));
                if (h == 0 || v == 0)
                    continue;

                aspectOffset = movieOffset + i + 4;
                horizontal = h;
                vertical = v;
            }

            return new Metadata(width, height, aspectOffset, horizontal, vertical);
        }

        internal static async Task<PreparedWallpaper> PrepareAsync(string path)
        {
            Metadata metadata = Inspect(path);
            if (!metadata.NeedsRepair)
                return new PreparedWallpaper(path, metadata.Width, metadata.Height, false);

            var sourceInfo = new FileInfo(path);
            string identity = string.Concat(Path.GetFullPath(path), "|",
                sourceInfo.Length, "|", sourceInfo.LastWriteTimeUtc.Ticks);
            string key = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(identity)))[..24];
            string cacheDirectory = Path.Combine(
                AppSettings.SettingsDirPath, "aspect-cache");
            string cachedPath = Path.Combine(cacheDirectory, key + ".mp4");

            await CacheGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(cachedPath) ||
                    new FileInfo(cachedPath).Length != sourceInfo.Length)
                {
                    await Task.Run(() =>
                    {
                        Directory.CreateDirectory(cacheDirectory);
                        string temporaryPath = cachedPath + "." +
                            Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            File.Copy(path, temporaryPath, overwrite: false);
                            using (var stream = new FileStream(temporaryPath,
                                FileMode.Open, FileAccess.Write, FileShare.None))
                            {
                                // Preserve the pixel ratio to ~0.001% without
                                // using values that overflow decoder internals.
                                double ratio = (double)metadata.HorizontalSpacing /
                                    metadata.VerticalSpacing;
                                uint denominator = (uint)Math.Min(50000,
                                    Math.Floor(60000 / ratio));
                                uint numerator = (uint)Math.Round(ratio * denominator);
                                Span<byte> normalized = stackalloc byte[8];
                                BinaryPrimitives.WriteUInt32BigEndian(
                                    normalized[..4], numerator);
                                BinaryPrimitives.WriteUInt32BigEndian(
                                    normalized[4..], denominator);
                                stream.Position = metadata.AspectValuesOffset;
                                stream.Write(normalized);
                                stream.Flush(flushToDisk: true);
                            }
                            File.Move(temporaryPath, cachedPath, overwrite: true);
                        }
                        finally
                        {
                            try { File.Delete(temporaryPath); } catch { }
                        }
                    }).ConfigureAwait(false);
                }
            }
            finally
            {
                CacheGate.Release();
            }

            return new PreparedWallpaper(
                cachedPath, metadata.Width, metadata.Height, true);
        }

        private static bool IsMp4Family(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);
        }
    }
}
