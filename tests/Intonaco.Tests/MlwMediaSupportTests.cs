using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    public sealed class MlwMediaSupportTests
    {
        private static readonly byte[] Mp4Signature =
            { 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
              (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0 };

        [Fact]
        public void DialogListsMlwWithoutPretendingEveryContainerWorks()
        {
            Assert.True(MediaFormatProbe.IsSupportedExtension("example.MLW"));
            Assert.True(MediaFormatProbe.IsUncommonContainer("example.mlw"));
            Assert.Contains("*.mlw", MediaFormatProbe.FileDialogFilter);
            Assert.False(MlwMediaSupport.IsMlw("example.mp4"));
        }

        [Fact]
        public async Task PlainVideoWithMlwExtensionIsImportedWithoutModifyingOriginal()
        {
            var root = Path.Combine(Path.GetTempPath(), "IntonacoMlwTest-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                string source = Path.Combine(root, "video.mlw");
                await File.WriteAllBytesAsync(source, Mp4Signature);
                string path = await MlwMediaSupport.PrepareAsync(source,
                    CancellationToken.None, Path.Combine(root, "cache"));
                Assert.EndsWith(".mp4", path);
                Assert.Equal(Mp4Signature, await File.ReadAllBytesAsync(path));
                Assert.Equal(Mp4Signature, await File.ReadAllBytesAsync(source));
                // The cache is reused: don't unnecessarily copy large videos.
                Assert.Equal(path, await MlwMediaSupport.PrepareAsync(source,
                    CancellationToken.None, Path.Combine(root, "cache")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task MlwZipContainingVideoIsSafelyExtracted()
        {
            var root = Path.Combine(Path.GetTempPath(), "IntonacoMlwTest-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                string source = Path.Combine(root, "wallpaper.mlw");
                using (var archive = ZipFile.Open(source, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("Wallpapers/loop.mp4");
                    using Stream stream = entry.Open();
                    stream.Write(Mp4Signature);
                }
                string path = await MlwMediaSupport.PrepareAsync(source,
                    CancellationToken.None, Path.Combine(root, "cache"));
                Assert.EndsWith(".mp4", path);
                Assert.Equal(Mp4Signature, await File.ReadAllBytesAsync(path));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task OpaqueMlwProvidesActionableErrorInsteadOfBlackVideo()
        {
            var root = Path.Combine(Path.GetTempPath(), "IntonacoMlwTest-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                string source = Path.Combine(root, "protected.mlw");
                await File.WriteAllBytesAsync(source, Encoding.ASCII.GetBytes("MLW proprietary format"));
                var ex = await Assert.ThrowsAsync<NotSupportedException>(
                    () => MlwMediaSupport.PrepareAsync(source, CancellationToken.None,
                        Path.Combine(root, "cache")));
                Assert.Contains("proprietary", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task MlwVideoWrapperWithVerifiableMp4PayloadIsUnwrapped()
        {
            var root = Path.Combine(Path.GetTempPath(), "IntonacoMlwTest-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                string source = Path.Combine(root, "wrapped.mlw");
                var payload = new byte[24];
                Array.Copy(Mp4Signature, payload, Mp4Signature.Length);
                using (var stream = File.Create(source))
                {
                    stream.Write(Encoding.ASCII.GetBytes("MLW.VIDEO"));
                    stream.Write(new byte[7]); // Bounded wrapper metadata.
                    stream.Write(payload);
                }

                Assert.Equal(16, MlwMediaSupport.FindEmbeddedMp4Offset(source));
                string extracted = await MlwMediaSupport.PrepareAsync(source,
                    CancellationToken.None, Path.Combine(root, "cache"));
                Assert.EndsWith(".mp4", extracted);
                Assert.Equal(payload, await File.ReadAllBytesAsync(extracted));
                Assert.Equal((byte)'M', (await File.ReadAllBytesAsync(source))[0]);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task CanceledImportDoesNotCreateDecoderOutput()
        {
            var root = Path.Combine(Path.GetTempPath(), "IntonacoMlwTest-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                string source = Path.Combine(root, "video.mlw");
                await File.WriteAllBytesAsync(source, Mp4Signature);
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => MlwMediaSupport.PrepareAsync(source, cts.Token,
                        Path.Combine(root, "cache")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
