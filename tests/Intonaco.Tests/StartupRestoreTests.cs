using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    public class StartupRestoreTests
    {
        [Theory]
        [InlineData(false, "\"C:\\Program Files\\Intonaco\\Intonaco.exe\"")]
        [InlineData(true, "\"C:\\Program Files\\Intonaco\\Intonaco.exe\" --minimized")]
        public void StartupCommand_RespectsSeparateSignInPreference(
            bool startMinimized, string expected)
        {
            Assert.Equal(expected, StartupManager.BuildStartupCommand(
                @"C:\Program Files\Intonaco\Intonaco.exe", startMinimized));
        }

        [Fact]
        public void ManualLaunch_DoesNotImplicitlyRequestMinimizedWindow()
        {
            Assert.False(StartupManager.ShouldStartMinimized(
                new[] { @"C:\Program Files\Intonaco\Intonaco.exe" }));
            Assert.False(StartupManager.ShouldStartMinimized(
                new[] { @"C:\Program Files\Intonaco\Intonaco.exe", "--open" }));
        }

        [Theory]
        [InlineData("--minimized")]
        [InlineData("--start-minimized")]
        [InlineData("-m")]
        [InlineData("/minimized")]
        public void ExplicitSignInLaunch_RequestsTrayMode(string argument)
        {
            Assert.True(StartupManager.ShouldStartMinimized(new[] {
                @"C:\Program Files\Intonaco\Intonaco.exe", argument }));
        }
    }
}
