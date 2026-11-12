# Intonaco native video core (Rust)

This is an **original Rust** component for Intonaco Wallpaper, kept separate
from the existing C# / WPF application. It does not inherit or copy Lively's
architecture or player source code.

## Current implemented scope

- Aspect-correct `Contain` and `Cover` viewport geometry in physical pixels.
- Frame presentation budgets respecting source cadence, display rate and user cap.
- Paused state signals no decoding or presentation work.
- Minimal C ABI with no WebView, browser, gRPC, or foreign media player.
- Unit tests for the user's 4K 16:9 HEVC wallpaper and ultrawide displays.

## Important: not a finished video player

**This crate does not decode or display video yet.** It must not replace the
existing Windows playback backend until an original native Windows Media
Foundation + Direct3D 11 implementation is finished, benchmarked and verified.

Planned native components (not yet implemented):
1. Media Foundation H.264/HEVC source selection and DXVA-capability probing.
2. GPU-first Direct3D 11 decoding and display with a bounded frame queue.
3. Independent player window, lock-free IPC and explicit renderer lifetime.
4. Pause policies: default instant resume; optional snapshot-and-release only
   after reliable black-frame-free capture has passed tests on real hardware.
5. End-to-end playback, seeking, loops, Explorer recovery and MSIX testing.

## Test

```bash
cargo test --manifest-path native/intonaco-video-core/Cargo.toml
```

No external crates are required for this foundation.
