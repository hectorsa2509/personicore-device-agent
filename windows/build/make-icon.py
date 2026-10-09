#!/usr/bin/env python3
"""
Generates the agent's two graphic resources from the Personicore mark:

  Resources/mark-NN.png  white silhouettes per size — the tray tints them by status
  Resources/app.ico    executable icon: paper plate + mark in pine ink

The mark is not copied as a bitmap: it is described as geometry (DOTS) and drawn.
That keeps it sharp both at 16 px in the tray and at 256 px in Explorer, which
rescaling the original 75x75 PNG could not do.

The geometry was measured on `personicore/pas/public/images/logo.png` (the official lockup,
whose mark takes the first 75 px): 25 dots on a checkerboard sub-grid with a 9.15 px
pitch, with the radius shrinking towards the edge. If the logo ever changes, measure
the centers and radii again and only this table changes.
"""

import struct
from pathlib import Path

from PIL import Image, ImageDraw

INK = (0x00, 0x1F, 0x00, 0xFF)      # pine of the mark, measured from the original
PAPER = (0xF7, 0xF4, 0xED, 0xFF)    # house bone paper
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
TRAY_SIZES = (16, 20, 24, 32, 40, 48, 64)
SUPERSAMPLE = 8

# (x, y, radius) in pixels of the 75x75 original, with the mark centered at (37, 37.5).
DOTS = [
    (37.00, 37.50, 5.77),
    (45.86, 46.56, 4.55), (27.56, 46.51, 4.52),
    (45.94, 28.10, 4.50), (27.75, 28.23, 4.49),
    (18.50, 37.50, 3.88), (55.50, 37.50, 3.88),
    (36.86, 19.22, 3.83), (36.92, 55.84, 3.82),
    (54.70, 55.91, 2.94), (54.66, 19.00, 2.93),
    (18.62, 55.84, 2.92), (18.50, 19.00, 2.89),
    (64.29, 46.50, 1.93), (9.71, 46.50, 1.93),
    (27.71, 9.50, 1.91), (27.50, 65.29, 1.88),
    (9.62, 28.38, 1.87), (45.85, 9.54, 1.87),
    (64.38, 28.38, 1.86), (46.08, 65.15, 1.83),
    (36.83, 0.83, 1.11), (73.50, 37.50, 1.08),
    (36.80, 74.40, 1.06), (0.50, 37.50, 1.06),
]

SOURCE_BOX = 75.0
SOURCE_CENTER = (37.0, 37.5)


def visible_dots(size: int) -> list[tuple[float, float, float]]:
    """
    Optical simplification per size. At 16 px the 25 dots turn into noise: the outer
    rings fall below one pixel and only add dirt. The mark is trimmed to the rings
    that still read and then reframed to fill the whole tile — what any icon family
    does by hand.
    """
    if size <= 20:
        floor = 3.8      # 9 dots: center, diagonals and axes
    elif size <= 32:
        floor = 2.85     # 13 dots: + outer diagonals
    elif size <= 64:
        floor = 1.8      # 21 puntos
    else:
        floor = 0.0      # la marca completa

    return [dot for dot in DOTS if dot[2] >= floor]


def draw_mark(size: int, color, margin: float = 0.06) -> Image.Image:
    """The mark alone, on transparent, drawn with supersampling."""
    scale = size * SUPERSAMPLE
    canvas = Image.new("RGBA", (scale, scale), (0, 0, 0, 0))
    pen = ImageDraw.Draw(canvas)

    dots = visible_dots(size)

    # Reframe: real half-width of what will be drawn, radii included.
    half = max(max(abs(x - SOURCE_CENTER[0]), abs(y - SOURCE_CENTER[1])) + r for x, y, r in dots)

    unit = scale * (1.0 - 2 * margin) / (2 * half)
    center = scale / 2.0

    for x, y, r in dots:
        cx = center + (x - SOURCE_CENTER[0]) * unit
        cy = center + (y - SOURCE_CENTER[1]) * unit
        rr = r * unit
        pen.ellipse((cx - rr, cy - rr, cx + rr, cy + rr), fill=color)

    return canvas.resize((size, size), Image.LANCZOS)


def rounded_plate(size: int, color, radius_ratio: float = 0.22) -> Image.Image:
    """Background plate of the executable icon: readable in light and dark themes."""
    scale = size * SUPERSAMPLE
    canvas = Image.new("RGBA", (scale, scale), (0, 0, 0, 0))
    pen = ImageDraw.Draw(canvas)
    pen.rounded_rectangle((0, 0, scale - 1, scale - 1), radius=scale * radius_ratio, fill=color)
    return canvas.resize((size, size), Image.LANCZOS)


def app_icon(size: int) -> Image.Image:
    plate = rounded_plate(size, PAPER)
    mark = draw_mark(size, INK, margin=0.16)
    return Image.alpha_composite(plate, mark)


def write_ico(path: Path, frames: list[Image.Image]) -> None:
    """
    ICO with each size as an embedded PNG. Windows supports it since Vista and it
    avoids hand-serializing the double-height BMP with its AND mask.
    """
    from io import BytesIO

    blobs = []
    for frame in frames:
        buffer = BytesIO()
        frame.save(buffer, format="PNG")
        blobs.append(buffer.getvalue())

    offset = 6 + 16 * len(blobs)
    directory = b""
    for frame, blob in zip(frames, blobs):
        side = frame.width if frame.width < 256 else 0
        directory += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)

    path.write_bytes(struct.pack("<HHH", 0, 1, len(blobs)) + directory + b"".join(blobs))


def main() -> None:
    resources = Path(__file__).resolve().parents[1] / "src" / "Personicore.DeviceAgent" / "Resources"
    resources.mkdir(parents=True, exist_ok=True)

    # White silhouettes for the tray: one PNG per size step, already simplified.
    # Rescaling a single one would lose exactly the simplification that keeps 16 px readable.
    written = []
    for size in TRAY_SIZES:
        target = resources / f"mark-{size}.png"
        draw_mark(size, (0xFF, 0xFF, 0xFF, 0xFF), margin=0.03).save(target, format="PNG")
        written.append(target)

    ico = resources / "app.ico"
    write_ico(ico, [app_icon(size) for size in ICO_SIZES])
    written.append(ico)

    for target in written:
        print(f"{target.name:14} {target.stat().st_size:>8,} bytes")


if __name__ == "__main__":
    main()
