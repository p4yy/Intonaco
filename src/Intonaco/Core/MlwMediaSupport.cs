using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Intonaco.Core
{
    /// <summary>
    /// A safe compatibility path for .mlw files that actually contain a
    /// standard video container or a ZIP-wrapped standard video.
    /// Proprietary/protected MLW variants are NOT decoded or bypassed.
    /// </summary>
    internal static class MlwMediaSupport
    {
        private const long MaxVideoBytes = 2L * 1024 * 1024 * 1024;
        private const int MaxArchiveEntries = 256;
        private static readonly SemaphoreSlim CacheGate = new(1, 1);

        internal static bool IsMlw(string path) =>
            string.Equals(Path.GetExtension(path), ".mlw",
                StringComparison.OrdinalIgnoreCase);

        internal static async Task<string> PrepareAsync(
            string original, CancellationToken cancel,
            string? cacheRoot = null)
        {
            if (!IsMlw(original))
                return original;

            cancel.ThrowIfCancellationRequested();
            var info = new FileInfo(original);
            if (!info.Exists || info.Length <= 0)
                throw new IOException("The MLW file is missing or empty.");

            var detected = NativeMediaEngine.IdentifyContainer(original);
            string extension = detected switch
            {
                NativeMediaEngine.Container.IsoBmff => ".mp4",
                NativeMediaEngine.Container.Matroska => ".mkv",
                NativeMediaEngine.Container.Avi => ".avi",
                NativeMediaEngine.Container.Asf => ".wmv",
                NativeMediaEngine.Container.MpegTransport => ".ts",
                NativeMediaEngine.Container.MpegProgram => ".mpeg",
                _ => string.Empty
            };

            // Some MLW files advertise the MLW.VIDEO header followed by
            // ordinary MP4 data. Only unwrap when a real ISO-BMFF ftyp
            // header can be verified; never guess that encrypted data is MP4.
            long embeddedMp4Offset = extension.Length == 0
                ? FindEmbeddedMp4Offset(original) : -1;
            if (embeddedMp4Offset >= 0)
                extension = ".mp4";

            bool zipWrapped = extension.Length == 0 && IsZipHeader(original);
            if (extension.Length == 0 && !zipWrapped)
                throw new NotSupportedException(
                    "This MLW uses a proprietary or unsupported package format. " +
                    "Intonaco Wallpaper can play MLW files containing standard videos, " +
                    "but cannot decode protected MLW content. If available, download " +
                    "the MP4 version from the wallpaper's original website.");

            if (info.Length > MaxVideoBytes && !zipWrapped)
                throw new IOException("The MLW video exceeds the safe import size limit.");

            // Preserve the original file and cache only a playable local copy.
            string identity = string.Concat(Path.GetFullPath(original), "|",
                info.Length, "|", info.LastWriteTimeUtc.Ticks);
            string key = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(identity)))[..24];
            string root = cacheRoot ?? Path.Combine(
                AppSettings.SettingsDirPath, "mlw-cache");

            await CacheGate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                // Entry selection must happen inside the open archive so no
                // ZipArchiveEntry can outlive the ZipArchive.
                using ZipArchive? archive = zipWrapped
                    ? ZipFile.OpenRead(original) : null;
                ZipArchiveEntry? entry = null;
                if (archive != null)
                {
                    if (archive.Entries.Count > MaxArchiveEntries)
                        throw new InvalidDataException("MLW archive contains too many items.");

                    entry = archive.Entries
                        .Where(e => IsPlayableVideoExtension(e.FullName) &&
                                    !string.IsNullOrEmpty(e.Name) &&
                                    e.Length > 0 && e.Length <= MaxVideoBytes &&
                                    e.CompressedLength > 0 &&
                                    e.Length / Math.Max(e.CompressedLength, 1L) <= 200)
                        .OrderByDescending(e => e.Length)
                        .FirstOrDefault();
                    if (entry == null)
                        throw new NotSupportedException(
                            "This MLW archive does not contain a supported video. " +
                            "Try downloading its original MP4 edition.");

                    extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                }

                string dest = Path.Combine(root, key + extension);
                long expected = entry?.Length ??
                    (embeddedMp4Offset >= 0
                        ? info.Length - embeddedMp4Offset : info.Length);
                if (File.Exists(dest) && new FileInfo(dest).Length == expected)
                    return dest;

                Directory.CreateDirectory(root);
                string temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var target = new FileStream(temp, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        if (entry != null)
                        {
                            using Stream input = entry.Open();
                            await input.CopyToAsync(target, 81920, cancel)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await using var source = new FileStream(original,
                                FileMode.Open, FileAccess.Read, FileShare.Read,
                                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            if (embeddedMp4Offset >= 0)
                                source.Position = embeddedMp4Offset;
                            await source.CopyToAsync(target, 81920, cancel)
                                .ConfigureAwait(false);
                        }
                    }

                    cancel.ThrowIfCancellationRequested();
                    if (new FileInfo(temp).Length != expected)
                        throw new InvalidDataException("Incomplete MLW video extraction.");
                    File.Move(temp, dest, overwrite: true);
                }
                finally
                {
                    try { File.Delete(temp); } catch (IOException) { }
                }

                return dest;
            }
            catch (InvalidDataException ex) when (zipWrapped)
            {
                throw new NotSupportedException(
                    "This MLW archive is damaged, encrypted, or not a standard ZIP video package. " +
                    "Try the MP4 download if the source offers it.", ex);
            }
            finally
            {
                CacheGate.Release();
            }
        }

        internal static bool IsPlayableVideoExtension(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".wmv", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Conservative compatibility for a known MLW.VIDEO header:
        /// copy only if the following payload has a valid ISO BMFF
        /// ftyp box. This does not decrypt or reverse protected media.
        /// </summary>
        internal static long FindEmbeddedMp4Offset(string path)
        {
            Span<byte> prefix = stackalloc byte[8192];
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 8192, FileOptions.SequentialScan);
            int count = file.Read(prefix);
            if (count < 25 ||
                !prefix[..9].SequenceEqual("MLW.VIDEO"u8))
                return -1;

            for (int i = 9; i <= count - 16; i++)
            {
                if (!prefix.Slice(i + 4, 4).SequenceEqual("ftyp"u8))
                    continue;

                uint length = BinaryPrimitives.ReadUInt32BigEndian(
                    prefix.Slice(i, 4));
                if (length >= 16 && length <= 4096 &&
                    (long)length <= file.Length - i)
                    return i;
            }
            return -1;
        }

        private static bool IsZipHeader(string path)
        {
            Span<byte> header = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            if (stream.Read(header) != 4)
                return false;
            return header[0] == (byte)'P' && header[1] == (byte)'K' &&
                   (header[2] == 3 || header[2] == 5 || header[2] == 7) &&
                   (header[3] == 4 || header[3] == 6 || header[3] == 8);
        }
    }
}
