using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    /// <summary>
    /// Structural release checks for a real WPF XAML view. We verify icons
    /// and compatibility controls without loading or animating any wallpaper.
    /// </summary>
    public sealed class ReleaseUiTests
    {
        private static string ReadRepoFile(params string[] path)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory);
                 dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(new[] { dir.FullName }.Concat(path).ToArray());
                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);
            }
            throw new FileNotFoundException("Release source missing: " + string.Join("/", path));
        }

        [Fact]
        public void EveryPrimarySectionHasAVisibleFluentIcon()
        {
            string markup = ReadRepoFile("src", "Intonaco", "Views", "MainWindow.xaml");
            XDocument document = XDocument.Parse(markup);
            XNamespace presentation =
                "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

            foreach (string section in new[]
            {
                "Now playing", "Your wallpapers", "Playback settings", "Preferences"
            })
            {
                var heading = document.Descendants(presentation + "TextBlock")
                    .FirstOrDefault(n => (string?)n.Attribute("Text") == section);
                Assert.NotNull(heading);

                var row = heading!.Parent;
                Assert.Equal(presentation + "StackPanel", row!.Name);
                Assert.Contains(row.Descendants(presentation + "TextBlock"),
                    node => (string?)node.Attribute("Style") ==
                            "{StaticResource SectionGlyphText}");
            }

            Assert.Contains("Discover wallpapers", markup);
            Assert.Contains("&#xE774;", markup);
            Assert.Contains("&#xE8B9;", markup);
            Assert.Contains("Choose video", markup);
            Assert.Contains("SettingGlyphText", markup);
        }

        [Fact]
        public void StandaloneFinalUi_HasGpuDefaultWithoutTestingOrExternalPlayerPrompts()
        {
            string markup = ReadRepoFile("src", "Intonaco", "Views", "MainWindow.xaml");
            XDocument document = XDocument.Parse(markup);
            XNamespace presentation =
                "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

            var backend = document.Descendants(presentation + "ComboBox")
                .Single(n => (string?)n.Attribute(x + "Name") == "BackendComboBox");
            var options = backend.Elements(presentation + "ComboBoxItem").ToArray();

            Assert.Equal(3, options.Length);
            Assert.Equal("GPU", (string?)options[0].Attribute("Tag"));
            Assert.Equal("True", (string?)options[0].Attribute("IsSelected"));
            Assert.Equal("Intonaco GPU", (string?)options[0].Attribute("Content"));
            Assert.Equal("Native", (string?)options[1].Attribute("Tag"));
            Assert.Equal("Windows", (string?)options[2].Attribute("Tag"));

            Assert.DoesNotContain("(test)", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Locate MPV", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MPV (optional)", markup, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Original FPS", markup);

            var fps = document.Descendants(presentation + "ComboBox")
                .Single(n => (string?)n.Attribute(x + "Name") == "FpsComboBox");
            Assert.Equal("Collapsed", (string?)fps.Attribute("Visibility"));
        }

        [Fact]
        public void NoExternalPlayerRequired_AndThemeProvidesVectorIconStyles()
        {
            Assert.Equal("GPU", new AppSettings().PlaybackBackend);
            Assert.Equal("GPU", AppSettings.NormalizeBackend("MPV"));
            Assert.Equal("Native", AppSettings.NormalizeBackend("Native"));
            Assert.Equal("Windows", AppSettings.NormalizeBackend("Windows"));

            string theme = ReadRepoFile("src", "Intonaco", "Themes", "IntonacoTheme.xaml");
            XDocument document = XDocument.Parse(theme);
            XNamespace presentation =
                "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            foreach (string key in new[] { "SectionGlyphText", "SettingGlyphText" })
                Assert.Contains(document.Descendants(presentation + "Style"),
                    n => (string?)n.Attribute(x + "Key") == key);
        }

        [Fact]
        public void InstallerAndApplicationMetadataUseSameFinalVersion()
        {
            string project = ReadRepoFile("src", "Intonaco", "Intonaco.csproj");
            var doc = XDocument.Parse(project);
            Assert.Equal("2.2.0",
                doc.Descendants("Version").Single().Value);
            Assert.Equal("2.2.0.0",
                doc.Descendants("FileVersion").Single().Value);
        }
    }
}
