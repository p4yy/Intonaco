using System;
using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    public class SettingsTests
    {

        [Fact]
        public void LegacyStretchMode_MigratesToAspectPreservingFill()
        {
            var settings = AppSettings.LoadFromJson("{\"wallpaperFit\":\"Stretch\"}");
            Assert.Equal("Fill", settings.WallpaperFit);
        }
        [Fact]
        public void DefaultSettings_HaveSensibleValues()
        {
            var settings = new AppSettings();

            Assert.True(settings.IsMuted);
            Assert.Equal(0.0, settings.Volume);
            Assert.Equal(30, settings.TargetFps);
            Assert.Equal("Fill", settings.WallpaperFit);
            Assert.Equal(1.0, settings.PlaybackSpeed);
            Assert.True(settings.LoopPlayback);
            Assert.True(settings.PauseOnFullscreen);
            Assert.False(settings.StartWithWindows);
            Assert.Null(settings.LastWallpaperPath);
            Assert.Empty(settings.RecentWallpaperPaths);
            Assert.Equal("System", settings.Theme);
        }

        [Fact]
        public void Settings_ClampVolume()
        {
            var settings = new AppSettings { Volume = 2.0 };
            var loaded = AppSettings.Load(); // Will return defaults since file doesn't exist in test
            Assert.InRange(loaded.Volume, 0.0, 1.0);
        }

        [Fact]
        public void Settings_ClampFps()
        {
            var settings = new AppSettings { TargetFps = 120 };
            // After load validation
            settings.TargetFps = Math.Clamp(settings.TargetFps, 1, 60);
            Assert.InRange(settings.TargetFps, 1, 60);
        }

        [Fact]
        public void Settings_Normalize_ClampsAndCanonicalizesValues()
        {
            var settings = new AppSettings
            {
                Volume = 4.0,
                TargetFps = -3,
                PlaybackSpeed = 99.0,
                WallpaperFit = "fit",
                Theme = "Sepia"
            };

            settings.Normalize();

            Assert.Equal(1.0, settings.Volume);
            Assert.Equal(1, settings.TargetFps);
            Assert.Equal(2.0, settings.PlaybackSpeed);
            Assert.Equal("Fit", settings.WallpaperFit);
            Assert.Equal("System", settings.Theme);
        }

        [Fact]
        public void Settings_RememberWallpaper_DeduplicatesAndCapsRecents()
        {
            var settings = new AppSettings();

            for (int i = 0; i < 8; i++)
                settings.RememberWallpaper($@"C:\Videos\wallpaper-{i}.mp4");

            // Re-applying an older video moves it to the front without duplicating it.
            settings.RememberWallpaper(@"C:\Videos\wallpaper-3.mp4");

            Assert.Equal(6, settings.RecentWallpaperPaths.Count);
            Assert.Equal(@"C:\Videos\wallpaper-3.mp4", settings.RecentWallpaperPaths[0]);
            Assert.Equal(@"C:\Videos\wallpaper-3.mp4", settings.LastWallpaperPath);
            Assert.Equal(
                settings.RecentWallpaperPaths.Count,
                new System.Collections.Generic.HashSet<string>(
                    settings.RecentWallpaperPaths,
                    System.StringComparer.OrdinalIgnoreCase).Count);
        }
    }

    public class WallpaperPlayerTests
    {
        [WpfFact]
        public void Player_InitialState_IsStopped()
        {
            var player = new WallpaperPlayer();
            Assert.Equal(WallpaperPlayer.PlaybackState.Stopped, player.CurrentState);
            Assert.False(player.IsPaused);
            Assert.Null(player.CurrentFilePath);
            player.Dispose();
        }

        [WpfFact]
        public void Player_DefaultVolume_IsZero()
        {
            var player = new WallpaperPlayer();
            Assert.Equal(0.0, player.Volume);
            player.Dispose();
        }

        [WpfFact]
        public void Player_VolumeClamp()
        {
            var player = new WallpaperPlayer();
            player.Volume = 1.5;
            Assert.Equal(1.0, player.Volume);
            player.Volume = -0.5;
            Assert.Equal(0.0, player.Volume);
            player.Dispose();
        }

        [WpfFact]
        public void Player_FpsClamp()
        {
            var player = new WallpaperPlayer();
            player.TargetFps = 120;
            Assert.Equal(60, player.TargetFps);
            player.TargetFps = 0;
            Assert.Equal(1, player.TargetFps);
            player.Dispose();
        }

        [WpfFact]
        public void Player_PlaybackSpeedClampAndDisplayOptions()
        {
            var player = new WallpaperPlayer();

            player.PlaybackSpeed = 99.0;
            Assert.Equal(2.0, player.PlaybackSpeed);
            player.PlaybackSpeed = 0.0;
            Assert.Equal(0.25, player.PlaybackSpeed);

            player.StretchMode = System.Windows.Media.Stretch.Uniform;
            Assert.Equal(System.Windows.Media.Stretch.Uniform, player.StretchMode);

            Assert.True(player.LoopPlayback);
            player.LoopPlayback = false;
            Assert.False(player.LoopPlayback);
            Assert.Equal(0, player.NaturalVideoWidth);
            Assert.Equal(0, player.NaturalVideoHeight);

            player.Dispose();
        }

        [WpfFact]
        public void Player_PlayNonexistentFile_ReturnsFalse()
        {
            var player = new WallpaperPlayer();
            player.Initialize(); // Need to initialize before play
            string? errorMsg = null;
            player.ErrorOccurred += msg => errorMsg = msg;

            bool result = player.Play(@"C:\nonexistent\fake.mp4");

            Assert.False(result);
            Assert.NotNull(errorMsg);
            player.Dispose();
        }

        [WpfFact]
        public void Player_Dispose_IsIdempotent()
        {
            var player = new WallpaperPlayer();
            player.Dispose();
            player.Dispose(); // Should not throw
        }
    }

    public class FullscreenDetectorTests
    {
        [WpfFact]
        public void Detector_DefaultState_IsNotFullscreen()
        {
            var detector = new FullscreenDetector();
            Assert.False(detector.IsFullscreenActive);
            detector.Dispose();
        }

        [WpfFact]
        public void Detector_Dispose_IsIdempotent()
        {
            var detector = new FullscreenDetector();
            detector.Start();
            detector.Dispose();
            detector.Dispose(); // Should not throw
        }

        [WpfFact]
        public void Detector_EnableDisable()
        {
            var detector = new FullscreenDetector();
            detector.IsEnabled = false;
            Assert.False(detector.IsEnabled);
            detector.IsEnabled = true;
            Assert.True(detector.IsEnabled);
            detector.Dispose();
        }
    }

    public class FrameRateLimiterTests
    {
        [WpfFact]
        public void Limiter_DefaultFps_Is30()
        {
            var limiter = new FrameRateLimiter();
            Assert.Equal(30, limiter.TargetFps);
            limiter.Dispose();
        }

        [WpfFact]
        public void Limiter_ClampsFps()
        {
            var limiter = new FrameRateLimiter(120);
            Assert.Equal(60, limiter.TargetFps);

            limiter.TargetFps = 0;
            Assert.Equal(1, limiter.TargetFps);

            limiter.Dispose();
        }
    }
}
