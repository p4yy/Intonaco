using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    public class OptimizationTests
    {
        [WpfFact]
        public void PlayerHost_NeverShowsASecondaryStaticFrameLayer()
        {
            var player = new WallpaperPlayer();
            try
            {
                var host = Assert.IsType<Grid>(player.CreateHost());

                Assert.Collection(
                    host.Children.Cast<UIElement>(),
                    child => Assert.IsType<MediaElement>(child),
                    child =>
                    {
                        var image = Assert.IsType<Image>(child);
                        Assert.Equal(Visibility.Collapsed, image.Visibility);
                        Assert.Null(image.Source);
                    });

                Assert.False(player.HasActiveMediaPipeline);
                Assert.False(player.IsSuspended);
                Assert.Null(player.PausedFrame);
                Assert.Equal(TimeSpan.Zero, player.Position);
            }
            finally
            {
                player.Dispose();
            }
        }

        [Theory]
        [InlineData(1920, 1080, 1.0)]
        [InlineData(3840, 2160, 1.0)]
        [InlineData(7680, 4320, 0.5)]
        [InlineData(5120, 1440, 0.75)]
        public void PausedFrameScale_BoundsOnlyVeryLargeFrames(
            int width,
            int height,
            double expected)
        {
            Assert.Equal(
                expected,
                WallpaperPlayer.CalculatePausedFrameScale(width, height),
                precision: 3);
        }

        [WpfFact]
        public void FullscreenDetector_StartIsIdempotentAndStopReleasesTimer()
        {
            var detector = new FullscreenDetector();
            try
            {
                detector.Start();
                detector.Start();
                Assert.True(detector.IsRunning);

                detector.Stop();
                Assert.False(detector.IsRunning);
            }
            finally
            {
                detector.Dispose();
            }
        }

        [Fact]
        public void PerformanceSnapshot_ReportsProcessFootprint()
        {
            var snapshot = PerformanceSnapshot.Capture();

            Assert.True(snapshot.WorkingSetBytes > 0);
            Assert.True(snapshot.PrivateBytes > 0);
            Assert.True(snapshot.ManagedBytes > 0);
            Assert.True(snapshot.ThreadCount > 0);
            Assert.True(snapshot.HandleCount > 0);
        }

        /// <summary>
        /// Manually pausing must stop animation updates without changing the
        /// rendering surface. A MediaElement that is closed, collapsed or
        /// replaced with a bitmap is exactly what exposed black borders.
        /// </summary>
        [WpfFact]
        public async Task Player_PauseKeepsRenderingSurfaceAndResumeDoesNotReopen()
        {
            string sample = CreateSampleVideo();

            EnsureApplication();

            var player = new WallpaperPlayer();
            var window = new Window
            {
                Content = player.CreateHost(),
                Width = 640,
                Height = 360,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                Task<bool> firstOpen = WaitForMediaOpenedAsync(player);
                Assert.True(player.Play(sample));
                Assert.True(await firstOpen,
                    "The generated uncompressed AVI should open through Media Foundation.");

                await Task.Delay(500);

                var host = Assert.IsType<Grid>(window.Content);
                var media = Assert.IsType<MediaElement>(host.Children[0]);
                var pausedLayer = Assert.IsType<Image>(host.Children[1]);
                Uri? openSource = media.Source;
                Assert.NotNull(openSource);

                int reopenCount = 0;
                player.MediaOpened += _ => reopenCount++;

                player.Pause();

                Assert.Equal(WallpaperPlayer.PlaybackState.Paused, player.CurrentState);
                Assert.False(player.IsSuspended);
                Assert.True(player.HasActiveMediaPipeline);
                Assert.Null(player.PausedFrame);
                Assert.Same(openSource, media.Source);
                Assert.Equal(Visibility.Visible, media.Visibility);
                Assert.Equal(Visibility.Collapsed, pausedLayer.Visibility);
                Assert.Null(pausedLayer.Source);

                // The clock is stopped, so no further animation updates.
                TimeSpan pausedPosition = player.Position;
                await Task.Delay(200);
                Assert.Equal(pausedPosition, player.Position);
                Assert.Equal(0, reopenCount);

                player.Resume();

                Assert.Equal(WallpaperPlayer.PlaybackState.Playing, player.CurrentState);
                Assert.True(player.HasActiveMediaPipeline);
                Assert.Same(openSource, media.Source);
                Assert.Equal(Visibility.Visible, media.Visibility);
                // Resume must not reopen the media source or seek.
                Assert.Equal(0, reopenCount);

                player.Stop();
                Assert.Equal(WallpaperPlayer.PlaybackState.Stopped, player.CurrentState);
                Assert.False(player.HasActiveMediaPipeline);
                Assert.False(player.IsSuspended);
                Assert.Null(player.PausedFrame);
            }
            finally
            {
                player.Dispose();
                window.Close();
            }
        }

        internal static string CreateSampleVideo()
        {
            const int width = 320;
            const int height = 180;
            const int framesPerSecond = 10;
            const int frameCount = 30;
            int stride = ((width * 3 + 3) / 4) * 4;
            int frameBytes = stride * height;

            string folder = Path.Combine(Path.GetTempPath(), "Intonaco.Tests");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "lifecycle-sample.avi");
            if (File.Exists(path) && new FileInfo(path).Length > frameBytes * frameCount)
                return path;

            static void WriteFourCC(BinaryWriter writer, string value)
            {
                writer.Write(Encoding.ASCII.GetBytes(value));
            }

            static void PatchSize(MemoryStream stream, BinaryWriter writer, long sizePosition, long currentPosition)
            {
                long end = stream.Position;
                stream.Position = sizePosition;
                writer.Write((uint)(end - currentPosition));
                stream.Position = end;
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

            WriteFourCC(writer, "RIFF");
            long riffSizePosition = stream.Position;
            writer.Write(0u);
            WriteFourCC(writer, "AVI ");

            WriteFourCC(writer, "LIST");
            long headerListSizePosition = stream.Position;
            writer.Write(0u);
            long headerListDataPosition = stream.Position;
            WriteFourCC(writer, "hdrl");

            WriteFourCC(writer, "avih");
            writer.Write(56u);
            writer.Write((uint)(1_000_000 / framesPerSecond));
            writer.Write((uint)(frameBytes * framesPerSecond));
            writer.Write(0u);        // Padding granularity
            writer.Write(0x10u);     // AVIF_HASINDEX
            writer.Write((uint)frameCount);
            writer.Write(0u);        // Initial frames
            writer.Write(1u);        // Streams
            writer.Write((uint)frameBytes);
            writer.Write((uint)width);
            writer.Write((uint)height);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);

            WriteFourCC(writer, "LIST");
            long streamListSizePosition = stream.Position;
            writer.Write(0u);
            long streamListDataPosition = stream.Position;
            WriteFourCC(writer, "strl");

            WriteFourCC(writer, "strh");
            writer.Write(56u);
            WriteFourCC(writer, "vids");
            WriteFourCC(writer, "DIB ");
            writer.Write(0u);        // Flags
            writer.Write(0u);        // Priority/language
            writer.Write(0u);        // Initial frames
            writer.Write(1u);        // Scale
            writer.Write((uint)framesPerSecond);
            writer.Write(0u);        // Start
            writer.Write((uint)frameCount);
            writer.Write((uint)frameBytes);
            writer.Write(uint.MaxValue); // Quality
            writer.Write(0u);        // Sample size
            writer.Write((short)0);
            writer.Write((short)0);
            writer.Write((short)width);
            writer.Write((short)height);

            WriteFourCC(writer, "strf");
            writer.Write(40u);
            writer.Write(40u);       // BITMAPINFOHEADER size
            writer.Write(width);
            writer.Write(height);    // Positive: bottom-up DIB
            writer.Write((short)1);  // Planes
            writer.Write((short)24); // Bits per pixel
            writer.Write(0u);        // BI_RGB
            writer.Write((uint)frameBytes);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0u);
            writer.Write(0u);

            PatchSize(stream, writer, streamListSizePosition, streamListDataPosition);
            PatchSize(stream, writer, headerListSizePosition, headerListDataPosition);

            WriteFourCC(writer, "LIST");
            long movieListSizePosition = stream.Position;
            writer.Write(0u);
            long movieListDataPosition = stream.Position;
            long movieFourCCPosition = stream.Position;
            WriteFourCC(writer, "movi");

            var frameData = new byte[frameBytes];
            var frameOffsets = new List<uint>(frameCount);

            for (int frame = 0; frame < frameCount; frame++)
            {
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    byte green = (byte)(y * 255 / (height - 1));
                    for (int x = 0; x < width; x++)
                    {
                        int pixel = row + x * 3;
                        frameData[pixel] = (byte)((x + frame * 8) % 256);     // Blue
                        frameData[pixel + 1] = green;                         // Green
                        frameData[pixel + 2] = (byte)(255 - x * 255 / (width - 1)); // Red
                    }
                }

                frameOffsets.Add((uint)(stream.Position - movieFourCCPosition));
                WriteFourCC(writer, "00db");
                writer.Write((uint)frameBytes);
                writer.Write(frameData);
                if ((frameBytes & 1) != 0)
                    writer.Write((byte)0);
            }

            PatchSize(stream, writer, movieListSizePosition, movieListDataPosition);

            WriteFourCC(writer, "idx1");
            writer.Write((uint)(frameCount * 16));
            foreach (uint offset in frameOffsets)
            {
                WriteFourCC(writer, "00db");
                writer.Write(0x10u); // AVIIF_KEYFRAME; every AVI frame here is raw
                writer.Write(offset);
                writer.Write((uint)frameBytes);
            }

            PatchSize(stream, writer, riffSizePosition, riffSizePosition + sizeof(uint));
            writer.Flush();

            File.WriteAllBytes(path, stream.ToArray());
            return path;
        }

        private static async Task<bool> WaitForMediaOpenedAsync(WallpaperPlayer player)
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void Opened(TimeSpan _) => completion.TrySetResult(true);
            void Failed(string _) => completion.TrySetResult(false);

            player.MediaOpened += Opened;
            player.ErrorOccurred += Failed;

            try
            {
                Task completed = await Task.WhenAny(
                    completion.Task,
                    Task.Delay(TimeSpan.FromSeconds(10)));
                return completed == completion.Task && completion.Task.Result;
            }
            finally
            {
                player.MediaOpened -= Opened;
                player.ErrorOccurred -= Failed;
            }
        }

        private static void EnsureApplication()
        {
            if (Application.Current == null)
            {
                _ = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
            }
        }
    }
}
