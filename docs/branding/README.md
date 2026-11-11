# Intonaco brand

The primary mark is `intonaco-mark.svg`: two open desktop-frame corners and a motion stroke. The SVG lockups use the same geometry and have transparent backgrounds.

The Windows UI renders this mark as WPF vector paths. This avoids separate per-theme bitmaps and remains sharp at high DPI.

Regenerate the Windows shell icon with `python tools/generate-icon.py` (requires Pillow). Existing raster branding masters in this directory are kept for historical reference but are no longer embedded in the application.

Use quiet typography and spacing. Avoid gradients, decorative badges, shadows, or effects that do not serve a purpose.
