using System;
using System.Runtime.InteropServices;
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
        public void BuiltInDefault_DoesNotRequireOptionalMpvExecutable()
        {
            var settings = new AppSettings();
            Assert.Equal("Native", settings.PlaybackBackend);
            Assert.Null(settings.MpvExecutablePath);
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
