using Intonaco.Core;
using Xunit;

namespace Intonaco.Tests
{
    public class WallpaperSourcesTests
    {
        [Theory]
        [InlineData("https://motionbgs.com/")]
        [InlineData("https://mylivewallpapers.com/")]
        [InlineData("https://moewalls.com/")]
        [InlineData("https://wallpaperwaifu.com/")]
        [InlineData("https://lwp.gg/")]
        [InlineData("https://www.pexels.com/search/videos/loop/")]
        public void OfficialCopyLinks_AreApproved(string url)
            => Assert.True(WallpaperSources.IsApproved(url));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("http://motionbgs.com/")]
        [InlineData("https://motionbgs.com.evil.test/")]
        [InlineData("javascript:alert(1)")]
        public void AlteredOrNonHttpsLinks_AreRejected(string? url)
            => Assert.False(WallpaperSources.IsApproved(url));
    }
}
