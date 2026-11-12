//! Native Intonaco video core.
//!
//! Independent Rust implementation. No Lively, mpv or VLC code is used.
//! This crate currently handles viewport geometry and presentation budgets.
//! Actual GPU decode and Direct3D 11 presentation are separate forthcoming
//! components, and are NOT claimed to exist in this first milestone.

use std::f64;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum AspectMode {
    /// Show every source pixel; may have unused space around the image.
    Contain = 0,
    /// Fill the entire monitor while preserving aspect ratio (center crop).
    Cover = 1,
}

#[derive(Clone, Copy, Debug, PartialEq)]
#[repr(C)]
pub struct VideoRect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

/// Defines the video destination rectangle in native pixel coordinates.
/// Preserves aspect ratio. The output may extend beyond the screen only in
/// Cover mode; the host must clip to the screen's physical-pixel viewport.
pub fn video_rect(
    source_width: u32,
    source_height: u32,
    screen_width: u32,
    screen_height: u32,
    mode: AspectMode,
) -> Option<VideoRect> {
    if source_width == 0 || source_height == 0 ||
       screen_width == 0 || screen_height == 0 {
        return None;
    }

    let sx = f64::from(screen_width) / f64::from(source_width);
    let sy = f64::from(screen_height) / f64::from(source_height);
    let scale = match mode {
        AspectMode::Contain => sx.min(sy),
        AspectMode::Cover => sx.max(sy),
    };

    let width = f64::from(source_width) * scale;
    let height = f64::from(source_height) * scale;

    Some(VideoRect {
        x: (f64::from(screen_width) - width) * 0.5,
        y: (f64::from(screen_height) - height) * 0.5,
        width,
        height,
    })
}

/// Distinguish decode cadence from output presentation cadence. This
/// conservative policy never asks for a faster frame rate than the source,
/// device or user's cap; it never invents frames. Actual hardware buffering
/// is selected by the future renderer.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(C)]
pub struct FrameBudget {
    pub present_fps: u32,
    pub interval_ns: u64,
    pub decoding_allowed: u32,
}

pub fn frame_budget(
    source_num: u32,
    source_den: u32,
    display_hz: u32,
    user_cap: u32,
    paused: bool,
) -> FrameBudget {
    if paused {
        return FrameBudget {
            present_fps: 0,
            interval_ns: 0,
            decoding_allowed: 0,
        };
    }

    let source_fps = if source_den == 0 || source_num == 0 {
        30
    } else {
        f64::from(source_num) / f64::from(source_den)
    };

    let output = source_fps
        .min(f64::from(display_hz.max(1)))
        .min(f64::from(user_cap.max(1)))
        .max(1.0);

    FrameBudget {
        present_fps: output.round().max(1.0) as u32,
        interval_ns: (1_000_000_000.0 / output).round() as u64,
        decoding_allowed: 1,
    }
}

/// Versioned C ABI allows the future Win32 host to communicate with a
/// Rust native DLL without linking WPF, gRPC, WebView or any Lively code.
#[no_mangle]
pub extern "C" fn intonaco_core_abi_version() -> u32 { 1 }

/// C-friendly output arguments: no heap allocation crosses the FFI boundary.
/// Returns 1 on success, 0 on invalid arguments.
#[no_mangle]
pub extern "C" fn intonaco_core_viewport(
    source_width: u32,
    source_height: u32,
    screen_width: u32,
    screen_height: u32,
    mode: u32,
    output: *mut VideoRect,
) -> u32 {
    if output.is_null() { return 0; }
    let mode = match mode {
        0 => AspectMode::Contain,
        1 => AspectMode::Cover,
        _ => return 0,
    };
    let Some(rect) = video_rect(
        source_width, source_height, screen_width, screen_height, mode
    ) else { return 0; };

    // SAFETY: contract requires a valid writable VideoRect pointer.
    unsafe { *output = rect; }
    1
}

#[no_mangle]
pub extern "C" fn intonaco_core_budget(
    source_num: u32,
    source_den: u32,
    display_hz: u32,
    user_cap: u32,
    paused: u32,
) -> FrameBudget {
    frame_budget(source_num, source_den, display_hz, user_cap, paused != 0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn square_pixel_4k_fills_4k_monitor_without_zoom() {
        let r = video_rect(3840, 2160, 3840, 2160, AspectMode::Cover).unwrap();
        assert_eq!(r, VideoRect { x: 0.0, y: 0.0, width: 3840.0, height: 2160.0 });
    }

    #[test]
    fn source_4k_on_1080p_keeps_entire_picture() {
        let r = video_rect(3840, 2160, 1920, 1080, AspectMode::Contain).unwrap();
        assert_eq!(r, VideoRect { x: 0.0, y: 0.0, width: 1920.0, height: 1080.0 });
    }

    #[test]
    fn ultrawide_cover_centers_crop_without_stretch() {
        let r = video_rect(3840, 2160, 3440, 1440, AspectMode::Cover).unwrap();
        assert!((r.width - 3440.0).abs() < 0.01);
        assert!(r.y < 0.0);
        assert!((r.height - 1935.0).abs() < 0.01);
    }

    #[test]
    fn ultrawide_contain_does_not_crop() {
        let r = video_rect(3840, 2160, 3440, 1440, AspectMode::Contain).unwrap();
        assert!((r.height - 1440.0).abs() < 0.01);
        assert!(r.x > 0.0);
    }

    #[test]
    fn invalid_dimensions_do_not_produce_nan_or_infinity() {
        assert!(video_rect(0, 2160, 1920, 1080, AspectMode::Cover).is_none());
        let mut out = VideoRect {x:0.,y:0.,width:0.,height:0.};
        assert_eq!(intonaco_core_viewport(0,2160,1920,1080,1,&mut out), 0);
        assert_eq!(intonaco_core_viewport(3840,2160,1920,1080,6,&mut out), 0);
        assert_eq!(intonaco_core_viewport(3840,2160,1920,1080,1,std::ptr::null_mut()), 0);
    }

    #[test]
    fn pause_budget_disallows_decode_and_composition() {
        let b = frame_budget(30, 1, 144, 30, true);
        assert_eq!(b.decoding_allowed, 0);
        assert_eq!(b.present_fps, 0);
    }

    #[test]
    fn do_not_raise_source_frame_rate_or_exceed_user_cap() {
        let b = frame_budget(24000, 1001, 144, 30, false);
        assert_eq!(b.present_fps, 24);
        let b = frame_budget(60, 1, 144, 24, false);
        assert_eq!(b.present_fps, 24);
        let b = frame_budget(120, 1, 60, 120, false);
        assert_eq!(b.present_fps, 60);
    }

    #[test]
    fn ffi_result_matches_native_geometry() {
        let mut out = VideoRect { x: 0.0, y: 0.0, width: 0.0, height: 0.0 };
        assert_eq!(intonaco_core_viewport(3840,2160,1920,1080,1,&mut out), 1);
        assert_eq!(out.width, 1920.0);
        assert_eq!(out.height, 1080.0);
        assert_eq!(intonaco_core_abi_version(), 1);
    }
}
