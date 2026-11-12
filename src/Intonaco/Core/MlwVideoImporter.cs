using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Intonaco.Core
{
    /// <summary>
    /// Imports the documented MLW.VIDEO v0/v1 container to a cached local MP4.
    /// The original file is never altered. Parsing/decryption runs off the UI
    /// thread and is strictly bounded to avoid untrusted large allocations.
    ///
    /// MLW uses AES-128-GCM and is not a codec. We only accept authenticated
    /// video payloads that actually contain an ISO-BMFF (MP4) container.
    /// Other types/versions fail explicitly rather than presenting black video.
    ///
    /// Interoperability format reference: MIT-licensed Wallnetic project,
    /// https://github.com/fatihkan/wallnetic (MLWDecryptor.swift).
    /// </summary>
    internal static class MlwVideoImporter
    {
        // A second full-file plaintext and ciphertext buffer is required by
        // .NET AesGcm. Limit total memory during import; never freeze the UI.
        internal const long MaximumSourceBytes = 256L * 1024 * 1024;
        private const string VideoMagic = "MLW.VIDEO";
        private const string DepthMagic = "MLW.DEPTH";
        private const uint MetadataBlock = 0x01010101;
        private const uint VideoBlock = 0x02020202;
        private static readonly byte[] VideoKey =
        {
            0xd2, 0x7e, 0x15, 0x46, 0x28, 0xae, 0x2b, 0xa6,
            0xab, 0x4b, 0x97, 0x75, 0x16, 0x5f, 0xf7, 0x37
        };

        internal static Task<string> PrepareAsync(
            string path, CancellationToken cancellationToken = default) =>
            Task.Run(() => PrepareFile(path, cancellationToken), cancellationToken);

        private static string PrepareFile(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 64)
                throw new InvalidDataException("This MLW file is missing or incomplete.");
            if (info.Length > MaximumSourceBytes)
                throw new NotSupportedException(
                    "This MLW is too large for safe in-app import (limit 256 MB). " +
                    "Use the site's standard MP4 download if available.");

            // File identity is stable for cache hits but never exposes paths in
            // generated cache filenames. A new source version gets a new key.
            string identity = string.Concat(
                Path.GetFullPath(path), "|", info.Length, "|",
                info.LastWriteTimeUtc.Ticks);
            string hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
            string cacheDir = Path.Combine(AppSettings.SettingsDirPath, "mlw-cache");
            string cached = Path.Combine(cacheDir, hash + ".mp4");
            if (File.Exists(cached) && new FileInfo(cached).Length >= 12 &&
                NativeMediaEngine.IdentifyContainer(cached) ==
                    NativeMediaEngine.Container.IsoBmff)
                return cached;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 65536, FileOptions.SequentialScan);

            Span<byte> header = stackalloc byte[10];
            stream.ReadExactly(header);
            if (header.SequenceEqual("MLW.DEPTH\0"u8))
                throw new NotSupportedException(
                    "MLW depth/parallax files are not video wallpapers. " +
                    "Choose an MLW.VIDEO wallpaper or a standard MP4.");
            if (!header.SequenceEqual("MLW.VIDEO\0"u8))
                throw new NotSupportedException(
                    "Unrecognized MLW container. This app supports " +
                    "MLW.VIDEO v0/v1, not every proprietary MLW variant.");

            uint version = ReadUInt32(stream);
            if (version > 1)
                throw new NotSupportedException(
                    "This MLW uses a newer unsupported container version.");
            if (ReadUInt32(stream) != MetadataBlock)
                throw new InvalidDataException("MLW metadata block is invalid.");
            ulong metaLength = ReadUInt64(stream);
            if (metaLength > (ulong)(stream.Length - stream.Position))
                throw new InvalidDataException("MLW metadata block is truncated.");
            stream.Position += (long)metaLength;

            if (stream.Length - stream.Position < 12 ||
                ReadUInt32(stream) != VideoBlock)
                throw new InvalidDataException("MLW video block is missing.");
            ulong dataLength = ReadUInt64(stream);
            if (dataLength < 44 || dataLength >
                (ulong)(stream.Length - stream.Position) ||
                dataLength > (ulong)MaximumSourceBytes)
                throw new InvalidDataException("MLW encrypted video length is invalid.");

            int cipherLength = checked((int)dataLength - 32);
            byte[] iv = new byte[16];
            stream.ReadExactly(iv);
            byte[] cipher = GC.AllocateUninitializedArray<byte>(cipherLength);
            byte[] plain = GC.AllocateUninitializedArray<byte>(cipherLength);
            byte[] tag = new byte[16];
            try
            {
                stream.ReadExactly(cipher);
                stream.ReadExactly(tag);
                token.ThrowIfCancellationRequested();
                try
                {
                    using var aes = new AesGcm(VideoKey, 16);
                    aes.Decrypt(iv.AsSpan(0, 12), cipher, tag, plain);
                }
                catch (CryptographicException ex)
                {
                    throw new NotSupportedException(
                        "This MLW cannot be authenticated. It may use a " +
                        "newer format/key or be damaged; download its MP4 version.", ex);
                }

                if (NativeMediaEngine.IdentifyContainer(
                    plain.AsSpan(0, Math.Min(512, plain.Length))) !=
                    NativeMediaEngine.Container.IsoBmff)
                    throw new InvalidDataException(
                        "The MLW payload is not an MP4 video.");

                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(cacheDir);
                string tempPath = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(tempPath, plain);
                    token.ThrowIfCancellationRequested();
                    File.Move(tempPath, cached, true);
                }
                finally
                {
                    try { File.Delete(tempPath); } catch { }
                }
                return cached;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cipher);
                CryptographicOperations.ZeroMemory(plain);
                CryptographicOperations.ZeroMemory(iv);
            }
        }

        private static uint ReadUInt32(Stream source)
        {
            Span<byte> bytes = stackalloc byte[4];
            source.ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt32BigEndian(bytes);
        }

        private static ulong ReadUInt64(Stream source)
        {
            Span<byte> bytes = stackalloc byte[8];
            source.ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt64BigEndian(bytes);
        }
    }
}
