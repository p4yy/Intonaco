// Geometry tests compile against the exact native engine logic.
// No video decoder or GPU is required to verify normalized crop math.
#include "engine.cpp"
#include <cmath>
#include <cstdio>
int main() {
    auto nearly = [](float a, double b) { return std::fabs(a - b) < 0.0001; };
    // Original 3840x2160 HEVC video must preserve its full 16:9 framing.
    auto same = calculateCrop(3840,2160,1920,1080,true);
    if (!nearly(same.source.left,0) ||
        !nearly(same.source.right,1) ||
        !nearly(same.source.top,0) ||
        !nearly(same.source.bottom,1)) return 1;
    // Wide videos crop sides for Fill, and preserve the correct center.
    auto wide = calculateCrop(2560,1080,1920,1080,true);
    const double expected = ((2560.0/1080.0)-(1920.0/1080.0))/(2.0*(2560.0/1080.0));
    if (!nearly(wide.source.left,expected) ||
        !nearly(wide.source.right,1.0-expected)) return 2;
    // 4:3 videos crop top/bottom for Fill.
    auto portrait = calculateCrop(1024,768,1920,1080,true);
    if (portrait.source.top <= 0 || portrait.source.bottom >= 1 ||
        !nearly(portrait.source.left,0) || !nearly(portrait.source.right,1)) return 3;
    // Fit must letterbox a 4:3 video instead of stretching it.
    auto fit = calculateCrop(1024,768,1920,1080,false);
    if (fit.destination.left <= 0 || fit.destination.right >= 1920 ||
        fit.destination.top != 0 || fit.destination.bottom != 1080) return 4;
    // Fit must pillarbox vertically for ultrawide content.
    auto fitWide = calculateCrop(2560,1080,1920,1080,false);
    if (fitWide.destination.top <= 0 || fitWide.destination.bottom >= 1080 ||
        fitWide.destination.left != 0 || fitWide.destination.right != 1920) return 5;
    std::puts("Native GPU crop/fit tests passed.");
    return 0;
}
