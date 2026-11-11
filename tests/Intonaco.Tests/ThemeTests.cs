using System;
using System.Collections;
using System.IO.Packaging;
using System.Linq;
using System.Net;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Intonaco.Views;
using Xunit;

namespace Intonaco.Tests
{
    public class ThemeTests
    {
        private static ResourceDictionary LoadTheme()
        {
            // The xUnit host has not created a WPF Application yet, so touch
            // PackUriHelper first to register the pack URI parser. Without
            // this, System.Uri treats "application:,,," as an invalid port.
            _ = PackUriHelper.UriSchemePack;
            _ = WebRequest.RegisterPrefix(
                PackUriHelper.UriSchemePack,
                new PackWebRequestFactory());

            // Application's static initialization registers WPF's internal
            // "application" request factory used to resolve component URIs.
            // Keep it alive across tests; closing a hosted test window must
            // not shut down the shared dispatcher.
            if (Application.Current == null)
            {
                _ = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
            }

            return new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/Intonaco;component/Themes/IntonacoTheme.xaml",
                    UriKind.Absolute)
            };
        }

        private static Window Host(FrameworkElement element)
        {
            var window = new Window
            {
                Content = element,
                Width = 360,
                Height = 140,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };

            window.Show();
            window.UpdateLayout();
            return window;
        }

        [WpfFact]
        public void SliderStyle_AppliesNativeTemplateAndBehavior()
        {
            var theme = LoadTheme();
            var style = Assert.IsType<Style>(theme["IntonacoSlider"]);
            var slider = new Slider { Style = style, Width = 240 };
            var window = Host(slider);

            try
            {
                // The hosted window has already applied the template during
                // layout; ApplyTemplate therefore correctly returns false.
                slider.ApplyTemplate();
                var track = Assert.IsType<Track>(
                    slider.Template.FindName("PART_Track", slider));

                Assert.NotNull(track.DecreaseRepeatButton);
                Assert.NotNull(track.IncreaseRepeatButton);
                Assert.NotNull(track.Thumb);
                Assert.Equal(22, track.Thumb.Width);
                Assert.Equal(22, track.Thumb.Height);
                Assert.Equal(32, slider.Height);
                Assert.True(slider.IsMoveToPointEnabled);
                Assert.True(slider.IsSnapToTickEnabled);
                Assert.Equal(1, slider.SmallChange);
                Assert.Equal(10, slider.LargeChange);
            }
            finally
            {
                window.Close();
            }
        }

        [WpfFact]
        public void ToggleStyle_UsesCompactSettingsSwitch()
        {
            var theme = LoadTheme();
            var style = Assert.IsType<Style>(theme["IntonacoToggle"]);
            var toggle = new CheckBox { Style = style, Content = "Setting", Width = 300 };
            var window = Host(toggle);

            try
            {
                toggle.ApplyTemplate();
                var track = Assert.IsType<Border>(
                    toggle.Template.FindName("Track", toggle));
                var thumb = Assert.IsType<Border>(
                    toggle.Template.FindName("Thumb", toggle));
                var switchGrid = Assert.IsType<Grid>(VisualTreeHelper.GetParent(track));

                Assert.Equal(40, switchGrid.Width);
                Assert.Equal(20, switchGrid.Height);
                Assert.Equal(12, thumb.Width);
                Assert.Equal(12, thumb.Height);
            }
            finally
            {
                window.Close();
            }
        }

        [Fact]
        public void BrandIcon_IsEmbedded()
        {
            const string fileName = "intonaco.ico";
        {
            var assembly = typeof(MainWindow).Assembly;
            using var stream = assembly.GetManifestResourceStream("Intonaco.g.resources");
            Assert.NotNull(stream);

            using var reader = new ResourceReader(stream);
            var keys = reader.Cast<DictionaryEntry>()
                .Select(entry => entry.Key.ToString())
                .ToList();

            Assert.Contains($"assets/{fileName.ToLowerInvariant()}", keys);
        }
    }
}
