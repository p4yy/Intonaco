using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    public class Mp4AspectSafetyTests
    {
        [Fact]
        public async Task UnreducedPixelAspect_RepairsOnlyTheCopy_NotTheSourceOrVideoBytes()
        {
            string folder = Path.Combine(Path.GetTempPath(), "Intonaco-Aspect-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "sample.mp4");

            try
            {
                byte[] original = CreateSample(16668180, 16648093);
                await File.WriteAllBytesAsync(path, original);

                var metadata = Mp4AspectSafety.Inspect(path);
                Assert.Equal(3840, metadata.Width);
                Assert.Equal(2160, metadata.Height);
                Assert.True(metadata.NeedsRepair);

                PreparedWallpaper result = await Mp4AspectSafety.PrepareAsync(path);
                Assert.True(result.CorrectedAspect);
                Assert.NotEqual(path, result.PlaybackPath);
                Assert.Equal(3840, result.SourceWidth);
                Assert.Equal(2160, result.SourceHeight);

                byte[] repaired = File.ReadAllBytes(result.PlaybackPath);
                Assert.Equal(original.Length, repaired.Length);
                Assert.Equal(original, File.ReadAllBytes(path));

                // Entire file is identical except for the eight 'pasp'
                // metadata bytes. 'mdat' (the encoded video) stays unchanged.
                int pasp = FindAscii(original, "pasp");
                Assert.True(pasp >= 0);
                for (int i = 0; i < original.Length; i++)
                {
                    if (i >= pasp + 4 && i < pasp + 12)
                        continue;
                    Assert.Equal(original[i], repaired[i]);
                }

                var repairedMetadata = Mp4AspectSafety.Inspect(result.PlaybackPath);
                Assert.False(repairedMetadata.NeedsRepair);
                double originalRatio = (double)metadata.HorizontalSpacing /
                    metadata.VerticalSpacing;
                double fixedRatio = (double)repairedMetadata.HorizontalSpacing /
                    repairedMetadata.VerticalSpacing;
                Assert.InRange(Math.Abs(originalRatio - fixedRatio), 0, 0.00002);

                PreparedWallpaper again = await Mp4AspectSafety.PrepareAsync(path);
                Assert.Equal(result.PlaybackPath, again.PlaybackPath);
            }
            finally
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
            }
        }

        [Theory]
        [InlineData(1u, 1u)]
        [InlineData(4u, 3u)]
        public async Task StandardPixelAspect_DoesNotMakeCopy(uint h, uint v)
        {
            string path = Path.Combine(Path.GetTempPath(),
                "Intonaco-Aspect-" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                File.WriteAllBytes(path, CreateSample(h, v));
                PreparedWallpaper result = await Mp4AspectSafety.PrepareAsync(path);
                Assert.False(result.CorrectedAspect);
                Assert.Equal(path, result.PlaybackPath);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        [Fact]
        public async Task OtherContainers_AreNotModified()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".webm");
            try
            {
                File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
                PreparedWallpaper result = await Mp4AspectSafety.PrepareAsync(path);
                Assert.Equal(path, result.PlaybackPath);
                Assert.False(result.CorrectedAspect);
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
        }

        private static int FindAscii(byte[] data, string value)
        {
            byte[] needle = Encoding.ASCII.GetBytes(value);
            for (int i = 0; i <= data.Length - needle.Length; i++)
                if (data.AsSpan(i, needle.Length).SequenceEqual(needle))
                    return i;
            return -1;
        }

        private static byte[] CreateSample(uint h, uint v)
        {
            byte[] avc1 = new byte[86];
            BinaryPrimitives.WriteUInt32BigEndian(avc1.AsSpan(0, 4), 86);
            Encoding.ASCII.GetBytes("avc1").CopyTo(avc1, 4);
            BinaryPrimitives.WriteUInt16BigEndian(avc1.AsSpan(32, 2), 3840);
            BinaryPrimitives.WriteUInt16BigEndian(avc1.AsSpan(34, 2), 2160);

            byte[] pasp = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(pasp.AsSpan(0, 4), 16);
            Encoding.ASCII.GetBytes("pasp").CopyTo(pasp, 4);
            BinaryPrimitives.WriteUInt32BigEndian(pasp.AsSpan(8, 4), h);
            BinaryPrimitives.WriteUInt32BigEndian(pasp.AsSpan(12, 4), v);

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            WriteBox(writer, "ftyp", Encoding.ASCII.GetBytes("isom"));
            WriteBox(writer, "mdat", new byte[] { 3, 1, 4, 1, 5, 9 });
            WriteBox(writer, "moov", avc1.Concat(pasp).ToArray());
            writer.Flush();
            return stream.ToArray();
        }

        private static void WriteBox(BinaryWriter writer, string kind, byte[] bytes)
        {
            byte[] size = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)bytes.Length + 8);
            writer.Write(size);
            writer.Write(Encoding.ASCII.GetBytes(kind));
            writer.Write(bytes);
        }
    }
}
