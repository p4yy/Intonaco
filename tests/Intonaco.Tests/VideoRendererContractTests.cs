using System;
using System.IO;
using Xunit;

namespace Intonaco.Tests
{
    /// <summary>
    /// Regression tests for the reported 4K-video resource and color issues.
    /// Do not reintroduce a fake FPS setting or force saturation/contrast.
    /// Hardware color fidelity still requires display-specific visual testing.
    /// </summary>
    public sealed class VideoRendererContractTests
    {
        [Fact]
        public void MediaElement_DoesNotSetAnimationTimelineFrameRate()
        {
            string player = ReadSource("src/Intonaco/Core/WallpaperPlayer.cs");
            string ui = ReadSource("src/Intonaco/Views/MainWindow.xaml.cs");

            Assert.DoesNotContain("SetValue(Timeline.DesiredFrameRateProperty", player);
            Assert.DoesNotContain("ClearValue(Timeline.DesiredFrameRateProperty", player);
            Assert.DoesNotContain("Timeline.DesiredFrameRateProperty", ui);
            Assert.Contains("FpsComboBox.IsEnabled = false", ui);
        }

        [Fact]
        public void Mpv_UsesColorManagementWithoutArtificialColorBoost()
        {
            string source = ReadSource("src/Intonaco/Core/MpvWallpaperPlayer.cs");

            Assert.Contains("\"--vo=gpu-next,gpu\"", source);
            Assert.Contains("\"--icc-profile-auto\"", source);
            Assert.DoesNotContain("\"--saturation=", source);
            Assert.DoesNotContain("\"--contrast=", source);
            Assert.DoesNotContain("\"--brightness=", source);
        }

        private static string ReadSource(string relativePath)
        {
            for (DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);
                 folder != null; folder = folder.Parent)
            {
                string path = Path.Combine(folder.FullName, relativePath);
                if (File.Exists(path))
                    return File.ReadAllText(path);
            }

            throw new FileNotFoundException(
                "Could not locate source file for regression test: " + relativePath);
        }
    }
}
