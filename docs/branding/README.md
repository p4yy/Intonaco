# Intonaco brand

The primary mark is `intonaco-mark.svg`: two open desktop-frame corners and a motion stroke. The SVG lockups use the same geometry and have transparent backgrounds.

The Windows UI renders this mark as WPF vector paths. This avoids separate per-theme bitmaps and remains sharp at high DPI.

Regenerate the Windows shell icon with `python tools/generate-icon.py` (requires Pillow). Existing raster branding masters in this directory are kept for historical reference but are no longer embedded in the application.

Use quiet typography and spacing. Avoid gradients, decorative badges, shadows, or effects that do not serve a purpose.

## Third-party brand marks in the About window

The About panel uses recognisable **GitHub**, **PayPal**, and **Ko-fi** vector
path data from [Simple Icons](https://github.com/simple-icons/simple-icons),
distributed under [CC0 1.0](https://github.com/simple-icons/simple-icons/blob/master/LICENSE.md).
The brand names and trademarks remain the property of their respective owners.
These marks identify external profile and donation destinations; they are
not Intonaco logos, app endorsements, or included media engines. The vectors
are embedded directly into native WPF markup, so no external fonts, SVG
renderer, network resources, or separate image files are required.
