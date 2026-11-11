using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    /// <summary>
    /// v2.0.0 behaviour tests: foreground auto-pause decisions, pre-flight
    /// media validation, settings migration, silent-start argument parsing and
    /// the frame-rate cap that is actually written onto the media element.
    /// </summary>
    public class ForegroundAutoPauseTests
    {
        [Theory]
        [InlineData("explorer")]
        [InlineData("Intonaco")]
        [InlineData("ShellExperienceHost")]
        [InlineData("StartMenuExperienceHost")]
        [InlineData("SearchHost")]
        [InlineData("ApplicationFrameHost")]
        [InlineData("LockApp")]
        public void DesktopAndShellProcesses_KeepTheWallpaperPlaying(string processName)
        {
            Assert.True(ForegroundDetector.ShouldPlayForForegroundProcess(processName));
        }

        [Theory]
        [InlineData("chrome")]
        [InlineData("Discord")]
        [InlineData("steam")]
        [InlineData("Code")]
        [InlineData("msedge")]
        [InlineData("devenv")]
        [InlineData("cyberpunk2077")]
        public void OtherApplications_PauseTheWallpaper(string processName)
        {
            Assert.False(ForegroundDetector.ShouldPlayForForegroundProcess(processName));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void UnknownForeground_FailsOpenSoTheWallpaperNeverVanishes(string? processName)
        {
            Assert.True(ForegroundDetector.ShouldPlayForForegroundProcess(processName));
        }

        [WpfFact]
        public void Detector_DefaultsToDesktopVisibleAndCanBeStopped()
        {
            var detector = new ForegroundDetector();
            try
            {
                Assert.True(detector.IsDesktopForeground);
                Assert.True(detector.IsEnabled);

                detector.Start();
                detector.Start(); // idempotent
                Assert.True(detector.IsRunning);

                detector.Stop();
                Assert.False(detector.IsRunning);
            }
            finally
            {
                detector.Dispose();
                detector.Dispose(); // idempotent
            }
        }

        [WpfFact]
        public void Detector_DisabledReportsDesktopVisibleSoPlaybackNeverSticksPaused()
        {
            var detector = new ForegroundDetector { IsEnabled = false };
            try
            {
                Assert.False(detector.IsRunning);
                Assert.True(detector.IsDesktopForeground);
            }
            finally
            {
                detector.Dispose();
            }
        }
    }

    public class MediaFormatProbeTests
    {
        [Theory]
        [InlineData("wallpaper.mp4")]
        [InlineData("clip.MP4")]
        [InlineData("movie.mkv")]
        [InlineData("loop.webm")]
        [InlineData("video.avi")]
        [InlineData("video.wmv")]
        [InlineData("video.mov")]
        public void RecognisedContainers_AreSupported(string fileName)
        {
            Assert.True(MediaFormatProbe.IsSupportedExtension(fileName));
            Assert.False(MediaFormatProbe.IsKnownUnsupportedExtension(fileName));
        }

        [Theory]
        [InlineData("clip.flv")]
        [InlineData("clip.rmvb")]
        [InlineData("clip.vob")]
        [InlineData("clip.ogv")]
        [InlineData("clip.divx")]
        [InlineData("clip.mxf")]
        [InlineData("clip.gif")]
        [InlineData("clip.webp")]
        public void CompatibilityFormats_AreAcceptedButFlaggedForConversion(string fileName)
        {
            Assert.True(MediaFormatProbe.IsSupportedExtension(fileName));
            Assert.True(MediaFormatProbe.RequiresCompatibilityConversion(fileName));
            Assert.True(MediaFormatProbe.IsKnownUnsupportedExtension(fileName));
            Assert.Contains("*" + Path.GetExtension(fileName),
                MediaFormatProbe.FileDialogFilter, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("movie.mkv")]
        [InlineData("loop.webm")]
        [InlineData("movie.mp4")]
        [InlineData("clip.avi")]
        [InlineData("clip.mts")]
        public void NativeCandidates_DoNotForceAnExpensiveConversion(string fileName)
        {
            Assert.True(MediaFormatProbe.IsSupportedExtension(fileName));
            Assert.False(MediaFormatProbe.RequiresCompatibilityConversion(fileName));
        }

        [Fact]
        public void CompatibilityFile_PassesPreflightWhenReadable()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "intonaco-test-" + Guid.NewGuid().ToString("N") + ".flv");
            File.WriteAllBytes(path, new byte[] { 0x46, 0x4C, 0x56, 0x01 });
            try
            {
                Assert.True(MediaFormatProbe.TryValidate(path, out string error));
                Assert.Equal(string.Empty, error);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData("Unable to play video: Unsupported codec or format", true)]
        [InlineData("Could not decode video", true)]
        [InlineData("0xC00D5212", true)]
        [InlineData("File not found", false)]
        [InlineData("Access denied", false)]
        public void CodecFailure_IsSeparatedFromFileAccessErrors(string error, bool expected)
        {
            Assert.Equal(expected, MediaCompatibility.IsLikelyDecoderFailure(error));
        }

        [Fact]
        public void MissingFile_IsRejectedWithAFindableMessage()
        {
            string missing = Path.Combine(
                Path.GetTempPath(), $"intonaco-missing-{Guid.NewGuid():N}.mp4");

            Assert.False(MediaFormatProbe.TryValidate(missing, out string error));
            Assert.Contains("File not found", error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EmptyFile_IsRejectedInsteadOfHangingTheDecoder()
        {
            string path = Path.Combine(
                Path.GetTempPath(), $"intonaco-empty-{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(path, Array.Empty<byte>());

            try
            {
                Assert.False(MediaFormatProbe.TryValidate(path, out string error));
                Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ReadableVideoFile_PassesPreflight()
        {
            string path = Path.Combine(
                Path.GetTempPath(), $"intonaco-readable-{Guid.NewGuid():N}.mp4");
            File.WriteAllBytes(path, new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 });

            try
            {
                Assert.True(MediaFormatProbe.TryValidate(path, out string error));
                Assert.Equal(string.Empty, error);
                Assert.Null(MediaFormatProbe.Validate(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankPath_IsRejected(string? path)
        {
            Assert.False(MediaFormatProbe.TryValidate(path, out string error));
            Assert.False(string.IsNullOrEmpty(error));
        }
    }

    public class SettingsMigrationTests
    {
        [Fact]
        public void NewSettings_EnableForegroundAutoPauseByDefault()
        {
            var settings = new AppSettings();

            Assert.True(settings.PauseWhenUnfocused);
            Assert.True(settings.IsAutoPauseEnabled);
        }

        [Fact]
        public void V1FileWithAutoPauseOff_DoesNotSilentlyEnableIt()
        {
            const string v1Json = """
            {
              "lastWallpaperPath": "C:\\Videos\\loop.mp4",
              "isMuted": true,
              "volume": 0,
              "targetFps": 30,
              "wallpaperFit": "Fill",
              "playbackSpeed": 1,
              "loopPlayback": true,
              "pauseOnFullscreen": false,
              "startWithWindows": false,
              "startMinimized": false,
              "theme": "System",
              "autoPlay": true
            }
            """;

            var settings = AppSettings.LoadFromJson(v1Json);

            Assert.False(settings.PauseWhenUnfocused);
            Assert.False(settings.IsAutoPauseEnabled);
            Assert.Equal("C:\\Videos\\loop.mp4", settings.LastWallpaperPath);
            Assert.Equal(30, settings.TargetFps);
        }

        [Fact]
        public void V1FileWithAutoPauseOn_GainsTheForegroundBehaviour()
        {
            const string v1Json = """
            {
              "targetFps": 60,
              "pauseOnFullscreen": true,
              "wallpaperFit": "fit"
            }
            """;

            var settings = AppSettings.LoadFromJson(v1Json);

            Assert.True(settings.PauseWhenUnfocused);
            Assert.True(settings.IsAutoPauseEnabled);
            Assert.Equal(60, settings.TargetFps);
            Assert.Equal("Fit", settings.WallpaperFit);
        }

        [Fact]
        public void V2FileWithAutoPauseOff_MigratesToContinueMode()
        {
            // A v2.0.x user who turned auto-pause off must not silently gain
            // the new default Mode A when the dedicated setting is added.
            const string v2Json = """
            {
              "pauseOnFullscreen": true,
              "pauseWhenUnfocused": false
            }
            """;

            var settings = AppSettings.LoadFromJson(v2Json);

            Assert.Equal(AppSettings.ContinueWhenUnfocusedMode, settings.BackgroundAnimationMode);
            Assert.False(settings.PauseAnimationsWhenUnfocused);
            Assert.False(settings.PauseWhenUnfocused);
            Assert.False(settings.PauseOnFullscreen);
            Assert.False(settings.IsAutoPauseEnabled);
        }

        [Fact]
        public void InvalidJson_FallsBackToDefaults()
        {
            var settings = AppSettings.LoadFromJson("{ this is not json");

            Assert.True(settings.PauseWhenUnfocused);
            Assert.Equal("Fill", settings.WallpaperFit);
        }
    }

    public class SilentStartTests
    {
        [Theory]
        [InlineData("--minimized")]
        [InlineData("-m")]
        [InlineData("--start-minimized")]
        [InlineData("/minimized")]
        [InlineData("--MINIMIZED")]
        public void MinimisedFlags_AreRecognised(string flag)
        {
            Assert.True(StartupManager.ShouldStartMinimized(
                new[] { @"C:\Program Files\Intonaco\Intonaco.exe", flag }));
        }

        [Fact]
        public void OrdinaryLaunch_DoesNotStartMinimised()
        {
            Assert.False(StartupManager.ShouldStartMinimized(
                new[] { @"C:\Program Files\Intonaco\Intonaco.exe" }));
            Assert.False(StartupManager.ShouldStartMinimized(
                new[] { @"C:\Program Files\Intonaco\Intonaco.exe", "--verbose" }));
        }
    }

    public class FrameRateCapTests
    {
        [Fact]
        public void Limiter_GatesFramesToTheConfiguredInterval()
        {
            using var limiter = new FrameRateLimiter(30);
            Assert.Equal(30, limiter.TargetFps);
            Assert.Equal(TimeSpan.FromSeconds(1.0 / 30.0), limiter.FrameInterval);

            var start = TimeSpan.FromSeconds(100);
            Assert.True(limiter.ShouldRender(start));
            Assert.False(limiter.ShouldRender(start + TimeSpan.FromMilliseconds(10)));
            Assert.False(limiter.ShouldRender(start + TimeSpan.FromMilliseconds(30)));
            Assert.True(limiter.ShouldRender(start + TimeSpan.FromMilliseconds(34)));
        }

        [Fact]
        public void Limiter_At60FpsAllowsTwiceAsManyFrames()
        {
            using var slow = new FrameRateLimiter(30);
            using var fast = new FrameRateLimiter(60);

            var start = TimeSpan.FromSeconds(100);
            int slowFrames = 0;
            int fastFrames = 0;

            // 1 ms ticks over one second, i.e. a 1000 Hz composition loop that
            // the gate has to thin out to the configured rate.
            for (int ms = 0; ms <= 1000; ms++)
            {
                if (slow.ShouldRender(start + TimeSpan.FromMilliseconds(ms))) slowFrames++;
                if (fast.ShouldRender(start + TimeSpan.FromMilliseconds(ms))) fastFrames++;
            }

            Assert.InRange(slowFrames, 28, 32);
            Assert.InRange(fastFrames, 56, 62);
            Assert.True(fastFrames > slowFrames * 1.5,
                $"60 fps should present far more frames than 30 fps (fast={fastFrames}, slow={slowFrames}).");
        }

        [Fact]
        public void Limiter_ReportsTheRateItActuallyAchieved()
        {
            using var limiter = new FrameRateLimiter(30);
            var start = TimeSpan.FromSeconds(0);

            // Five seconds of 1 ms ticks so the 2 s measurement window closes.
            for (int ms = 0; ms <= 5000; ms++)
                limiter.ShouldRender(start + TimeSpan.FromMilliseconds(ms));

            Assert.InRange(limiter.MeasuredFps, 25.0, 35.0);

            limiter.Reset();
            Assert.Equal(0.0, limiter.MeasuredFps);
        }

        [WpfFact]
        public void Player_WritesTheFrameRateCapOntoTheMediaElement()
        {
            var player = new WallpaperPlayer();
            try
            {
                var host = (Grid)player.CreateHost();
                var media = Assert.IsType<MediaElement>(host.Children[0]);

                // The cap is a local value on the MediaElement, which owns the
                // media clock. It must track the setting exactly, and 60 fps
                // must remove it so WPF presents at its normal rate.
                player.TargetFps = 30;
                Assert.Equal((object)30, media.GetValue(Timeline.DesiredFrameRateProperty));

                player.TargetFps = 15;
                Assert.Equal((object)15, media.GetValue(Timeline.DesiredFrameRateProperty));

                player.TargetFps = 60;
                object? mediaCap = media.GetValue(Timeline.DesiredFrameRateProperty);
                Assert.True(
                    mediaCap is null || (mediaCap is int mediaFps && mediaFps >= 60),
                    $"60 fps must clear the media cap but found {mediaCap}.");
            }
            finally
            {
                player.Dispose();
            }
        }

        [WpfFact]
        public void Player_StopsTheFrameGateWhenNotPlaying()
        {
            var player = new WallpaperPlayer();
            try
            {
                player.CreateHost();
                player.TargetFps = 30;

                // Nothing is playing, so no measured rate may accumulate.
                Assert.Equal(0.0, player.MeasuredFps);
                Assert.Equal(WallpaperPlayer.PlaybackState.Stopped, player.CurrentState);
            }
            finally
            {
                player.Dispose();
            }
        }
    }
}
