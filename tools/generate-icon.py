#!/usr/bin/env python3
"""Generate the Intonaco Windows icon from its two-corner vector mark.

Requires Pillow. The application header uses native WPF vectors and needs
no raster branding files.
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
TARGET = ROOT / "src" / "Intonaco" / "Assets" / "intonaco.ico"
SCALE = 8
D = 64 * SCALE


def pts(points):
    return [(round(x * SCALE), round(y * SCALE)) for x, y in points]


def stroke(draw, points, color):
    points = pts(points)
    width = 5 * SCALE
    draw.line(points, fill=color, width=width, joint="curve")
    radius = width // 2
    for x, y in (points[0], points[-1]):
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)


def generate():
    image = Image.new("RGBA", (D, D))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle(
        (3*SCALE, 3*SCALE, 61*SCALE, 61*SCALE),
        radius=14*SCALE, fill="#17242A",
    )
    stroke(draw, [
        (18, 43), (18, 24), (18.25, 21.8), (19.3, 19.6),
        (21.5, 18.3), (24, 18), (44, 18),
    ], "#83DFCC")
    stroke(draw, [
        (46, 21), (46, 40), (45.75, 42.2), (44.7, 44.4),
        (42.5, 45.7), (40, 46), (20, 46),
    ], "#F0F7F5")
    stroke(draw, [
        (25, 37), (27, 33), (29, 30), (31, 29.5),
        (34, 29.5), (36, 31), (38, 33),
    ], "#83DFCC")
    return image


if __name__ == "__main__":
    TARGET.parent.mkdir(parents=True, exist_ok=True)
    generate().save(
        TARGET, format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48),
               (64, 64), (128, 128), (256, 256)],
    )
    print(f"Wrote {TARGET}")
