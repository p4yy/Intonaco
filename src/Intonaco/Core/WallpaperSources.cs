using System;
using System.Collections.Generic;

namespace Intonaco.Core
{
    /// <summary>
    /// Curated third-party video sources. No embedded browser, remote scripts,
    /// telemetry, scraping or network traffic is involved in copying a link.
    /// The user downloads a supported local video directly from the source.
    /// </summary>
    public static class WallpaperSources
    {
        private static readonly HashSet<string> Approved = new(StringComparer.Ordinal)
        {
            "https://motionbgs.com/",
            "https://mylivewallpapers.com/",
            "https://moewalls.com/",
            "https://wallpaperwaifu.com/",
            "https://lwp.gg/",
            "https://www.pexels.com/search/videos/loop/"
        };

        public static bool IsApproved(string? url) =>
            !string.IsNullOrWhiteSpace(url) && Approved.Contains(url);
    }
}
