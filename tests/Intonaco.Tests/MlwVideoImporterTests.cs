using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    public sealed class MlwVideoImporterTests
    {
        private static readonly byte[] Key =
        {
            0xd2, 0x7e, 0x15, 0x46, 0x28, 0xae, 0x2b, 0xa6,
            0xab, 0x4b, 0x97, 0x75, 0x16, 0x5f, 0xf7, 0x37
        };

        [Fact]
        public async Task AuthenticatedVideo_ImportsAsCacheableMp4()
        {
            byte[] mp4 = FakeMp4();
            string input = TempFile(BuildMlw(mp4), ".mlw");
            string? imported = null;
            try
            {
                Assert.True(MediaFormatProbe.IsSupportedExtension(input));
                imported = await NativeMediaEngine.PrepareAsync(input)
                    .ContinueWith(t => t.Result.PlaybackPath);
                Assert.EndsWith(".mp4", imported, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(mp4, File.ReadAllBytes(imported));
                Assert.Equal(imported, (await MlwVideoImporter.PrepareAsync(input)));
            }
            finally
            {
                File.Delete(input);
                if (imported != null) File.Delete(imported);
            }
        }

        [Fact]
        public async Task TamperedCiphertext_FailsAuthenticationAndDoesNotProduceVideo()
        {
            byte[] container = BuildMlw(FakeMp4());
            container[^17] ^= 0x40;
            string input = TempFile(container, ".mlw");
            try
            {
                var error = await Assert.ThrowsAsync<NotSupportedException>(
                    () => MlwVideoImporter.PrepareAsync(input));
                Assert.Contains("authenticated", error.Message,
                    StringComparison.OrdinalIgnoreCase);
            }
            finally { File.Delete(input); }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(12)]
        [InlineData(34)]
        [InlineData(50)]
        public async Task TruncatedContainer_NeverLeaksPartiallyDecryptedFiles(int bytes)
        {
            string input = TempFile(BuildMlw(FakeMp4())[..bytes], ".mlw");
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(
                    () => MlwVideoImporter.PrepareAsync(input));
            }
            finally { File.Delete(input); }
        }

        [Fact]
        public async Task UnrecognizedMlwContainer_IsReportedClearly()
        {
            string input = TempFile(new byte[100], ".mlw");
            try
            {
                var ex = await Assert.ThrowsAsync<NotSupportedException>(
                    () => MlwVideoImporter.PrepareAsync(input));
                Assert.Contains("Unrecognized MLW", ex.Message);
            }
            finally { File.Delete(input); }
        }

        [Fact]
        public async Task Cancellation_PreventsImporterWork()
        {
            string input = TempFile(BuildMlw(FakeMp4()), ".mlw");
            try
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => MlwVideoImporter.PrepareAsync(input, cancelled.Token));
            }
            finally { File.Delete(input); }
        }

        private static byte[] FakeMp4()
        {
            byte[] data = new byte[40];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 24);
            Encoding.ASCII.GetBytes("ftyp").CopyTo(data, 4);
            Encoding.ASCII.GetBytes("isom").CopyTo(data, 8);
            return data;
        }

        private static byte[] BuildMlw(byte[] payload)
        {
            byte[] iv = new byte[16];
            RandomNumberGenerator.Fill(iv.AsSpan(0, 12));
            byte[] ciphertext = new byte[payload.Length];
            byte[] tag = new byte[16];
            using (var aes = new AesGcm(Key, 16))
                aes.Encrypt(iv.AsSpan(0, 12), payload, ciphertext, tag);

            using var output = new MemoryStream();
            output.Write(Encoding.ASCII.GetBytes("MLW.VIDEO\0"));
            WriteBe32(output, 1);
            WriteBe32(output, 0x01010101);
            WriteBe64(output, 0);
            WriteBe32(output, 0x02020202);
            WriteBe64(output, (ulong)payload.Length + 32);
            output.Write(iv);
            output.Write(ciphertext);
            output.Write(tag);
            return output.ToArray();
        }

        private static string TempFile(byte[] bytes, string extension)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "intonaco-test-" + Guid.NewGuid().ToString("N") + extension);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static void WriteBe32(Stream output, uint v)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, v);
            output.Write(bytes);
        }

        private static void WriteBe64(Stream output, ulong v)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, v);
            output.Write(bytes);
        }
    }
}
