using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    public sealed class NativeMfPlayBackendTests
    {
        [WpfFact]
        public void NativeHost_DoesNotStartASecondProcessOrDecoderWhenConstructed()
        {
            using var player = new NativeMfPlayWallpaperPlayer();
            var host = player.CreateHost();
            Assert.IsType<NativeVideoWindowHost>(host);
            Assert.IsAssignableFrom<HwndHost>(host);
            Assert.Same(host, player.CreateHost());
            Assert.Null(player.CurrentFilePath);
            Assert.Equal(TimeSpan.Zero, player.Duration);
            Assert.Equal(WallpaperPlayer.PlaybackState.Stopped, player.CurrentState);
        }

        [Fact]
        public void ComAbi_PropVariantAndEventHeaderAreCorrectForWinX64()
        {
            Assert.Equal(24, Marshal.SizeOf<NativeMfPlay.NativePropVariant>());
            Assert.Equal(32, Marshal.SizeOf<NativeMfPlay.EventHeader>());
            Assert.Equal(IntPtr.Zero, (IntPtr)0);
            var timestamp = TimeSpan.FromSeconds(6.25);
            var variant = NativeMfPlay.NativePropVariant.FromTime(timestamp);
            Assert.Equal(timestamp, variant.AsTime());
        }

        [Theory]
        [InlineData(Stretch.Uniform, 3840, 2160, 1920, 1200, 0, 1, 0, 1)]
        [InlineData(Stretch.UniformToFill, 3840, 2160, 1920, 1200, 0.05, 0.95, 0, 1)]
        [InlineData(Stretch.UniformToFill, 1920, 1200, 1920, 1080, 0, 1, 0.05, 0.95)]
        [InlineData(Stretch.UniformToFill, 3840, 2160, 1920, 1080, 0, 1, 0, 1)]
        public void NativeVideoCrop_PreservesOriginalGeometry(
            Stretch mode, int videoWidth, int videoHeight,
            int screenWidth, int screenHeight,
            double left, double right, double top, double bottom)
        {
            var rect = NativeMfPlayWallpaperPlayer.CalculateSourceCrop(
                mode, videoWidth, videoHeight, screenWidth, screenHeight);
            Assert.Equal(left, rect.Left, precision: 3);
            Assert.Equal(right, rect.Right, precision: 3);
            Assert.Equal(top, rect.Top, precision: 3);
            Assert.Equal(bottom, rect.Bottom, precision: 3);
        }

        [Fact]
        public void GpuDefault_DoesNotRequireOptionalMpvExecutable()
        {
            var settings = new AppSettings();
            Assert.Equal("GPU", settings.PlaybackBackend);
            Assert.Null(settings.MpvExecutablePath);
        }

        [WpfFact]
        public async Task NativePlayer_OpensAndPausesWithNoExternalApp()
        {
            // This fixture is generated locally by another regression test.
            // It does not download a codec, executable or video.
            string video = OptimizationTests.CreateSampleVideo();
            if (Application.Current == null)
                _ = new Application();

            using var player = new NativeMfPlayWallpaperPlayer();
            var window = new Window
            {
                Width = 480,
                Height = 270,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = player.CreateHost()
            };

            var opened = new TaskCompletionSource<TimeSpan>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            player.MediaOpened += duration => opened.TrySetResult(duration);
            player.ErrorOccurred += error =>
                opened.TrySetException(new InvalidOperationException(error));

            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.True(player.Play(video));

                Task winner = await Task.WhenAny(
                    opened.Task, Task.Delay(TimeSpan.FromSeconds(12)));
                Assert.Same(opened.Task, winner);
                await opened.Task;

                Assert.True(player.NaturalVideoWidth > 0);
                Assert.True(player.NaturalVideoHeight > 0);
                Assert.Equal(WallpaperPlayer.PlaybackState.Playing, player.CurrentState);

                player.Pause();
                Assert.Equal(WallpaperPlayer.PlaybackState.Paused, player.CurrentState);
                player.Resume();
                Assert.Equal(WallpaperPlayer.PlaybackState.Playing, player.CurrentState);
            }
            finally
            {
                player.Stop();
                window.Close();
            }
        }

        [WpfFact]
        public void StoppingUninitializedNativePlayerIsSafe()
        {
            using var player = new NativeMfPlayWallpaperPlayer();
            player.CreateHost();
            player.Stop();
            player.Stop();
            Assert.Equal(WallpaperPlayer.PlaybackState.Stopped, player.CurrentState);
        }
    }
}
