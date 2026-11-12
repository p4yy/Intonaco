using System;
using System.Diagnostics;
using System.Windows;

namespace Intonaco.Views
{
    public partial class AboutWindow : Window
    {
        private const string GitHubUrl = "https://github.com/p4yy/Intonaco";
        private const string PayPalUrl = "https://www.paypal.com/paypalme/aceevisuals";
        private const string KoFiUrl = "https://ko-fi.com/acevisualsco";

        public AboutWindow()
        {
            InitializeComponent();
            var version = typeof(AboutWindow).Assembly.GetName().Version;
            AboutVersionText.Text = version == null
                ? "Video wallpapers for Windows"
                : string.Format("Version {0}.{1}.{2} · Windows", version.Major,
                    version.Minor, version.Build);
        }

        private void OnOpenGitHub(object sender, RoutedEventArgs e) => Open(GitHubUrl);
        private void OnOpenPayPal(object sender, RoutedEventArgs e) => Open(PayPalUrl);
        private void OnOpenKoFi(object sender, RoutedEventArgs e) => Open(KoFiUrl);
        private void OnClose(object sender, RoutedEventArgs e) => Close();

        private void Open(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open the link: " + ex.Message,
                    "Intonaco Wallpaper", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
