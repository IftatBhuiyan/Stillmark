#!/usr/bin/env python3
"""Regenerate Stillmark's flat app-icon files from the Icon Composer sources.

The layered source of truth is mac/Icon/Stillmark.icon (paper glyph at 70% over
an ivory tile, navy tile in dark mode). macOS compiles that bundle directly in
scripts/build-mac.sh. Everything that needs a flat bitmap comes from here:

  app/src/WinShot.App/Assets/winshot.ico         Windows exe, tray, installer
  app/src/WinShot.App/Assets/winshot-master.png  Windows in-app logo
  mac/Icon/Stillmark-flat.png                    Mac .icns fallback (older Xcode)
  website/assets/app-icon.png                    Website logo and favicon

Requires Pillow:  python3 -m pip install pillow
Usage:            python3 scripts/generate-icons.py [--preview DIR]
"""
from __future__ import annotations

import argparse
import struct
from io import BytesIO
from pathlib import Path

from PIL import Image, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "mac/Icon/Stillmark.icon/Assets"
GLYPH = ASSETS / "Stillmark.png"
TILE = ASSETS / "Empty ivory.png"

GLYPH_SCALE = 0.7          # matches icon.json
SMALL_GLYPH_SCALE = 0.9    # 16-32 px: the 70% glyph is too fine to read
SMALL_MAX = 32
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def solid_mask(image: Image.Image) -> Image.Image:
    return image.getchannel("A").point(lambda value: 255 if value > 200 else 0)


def clean(image: Image.Image, grow: int) -> Image.Image:
    """Drop stray background-removal specks: keep alpha only near the solid shape."""
    keep = solid_mask(image).filter(ImageFilter.MaxFilter(grow * 2 + 1))
    alpha = Image.composite(image.getchannel("A"), Image.new("L", image.size, 0), keep)
    result = image.copy()
    result.putalpha(alpha)
    return result


def compose(glyph_scale: float) -> Image.Image:
    """Glyph centred on the ivory tile, same canvas as the sources."""
    tile = clean(Image.open(TILE).convert("RGBA"), grow=10)
    glyph = clean(Image.open(GLYPH).convert("RGBA"), grow=10)
    size = round(tile.width * glyph_scale)
    glyph = glyph.resize((size, size), Image.LANCZOS)
    layer = Image.new("RGBA", tile.size, (0, 0, 0, 0))
    # Centre the glyph's solid shape on the tile's solid shape; the artwork
    # sits a few pixels off-centre in both source canvases.
    tile_box = solid_mask(tile).getbbox()
    glyph_box = solid_mask(glyph).getbbox()
    x = round((tile_box[0] + tile_box[2]) / 2 - (glyph_box[0] + glyph_box[2]) / 2)
    y = round((tile_box[1] + tile_box[3]) / 2 - (glyph_box[1] + glyph_box[3]) / 2)
    layer.paste(glyph, (x, y))
    # The glyph may only draw on the tile, so everything outside stays transparent.
    layer.putalpha(Image.composite(layer.getchannel("A"), Image.new("L", tile.size, 0), solid_mask(tile)))
    return Image.alpha_composite(tile, layer)


def tight(image: Image.Image, pad: int = 6) -> Image.Image:
    """Square crop around the tile so the icon fills its box (Windows, web)."""
    left, top, right, bottom = solid_mask(image).getbbox()
    side = max(right - left, bottom - top) + pad * 2
    cx, cy = (left + right) / 2, (top + bottom) / 2
    box = (round(cx - side / 2), round(cy - side / 2))
    out = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    out.paste(image, (-box[0], -box[1]))
    return out


def resized(image: Image.Image, size: int) -> Image.Image:
    # Premultiply so transparent pixels do not bleed dark fringes into the edge.
    premultiplied = image.convert("RGBa").resize((size, size), Image.LANCZOS)
    return premultiplied.convert("RGBA")


def write_ico(path: Path, frames: dict[int, Image.Image]) -> None:
    """Multi-size .ico with one PNG-encoded frame per size, smallest first."""
    payloads = []
    for size in sorted(frames):
        buffer = BytesIO()
        frames[size].save(buffer, "PNG", optimize=True)
        payloads.append((size, buffer.getvalue()))
    header = struct.pack("<HHH", 0, 1, len(payloads))
    offset = len(header) + 16 * len(payloads)
    directory = b""
    for size, data in payloads:
        directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    path.write_bytes(header + directory + b"".join(data for _, data in payloads))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--preview", type=Path, help="also write each .ico frame as a PNG here")
    args = parser.parse_args()

    regular = compose(GLYPH_SCALE)
    regular_tight = tight(regular)
    small_tight = tight(compose(SMALL_GLYPH_SCALE))

    frames = {
        size: resized(small_tight if size <= SMALL_MAX else regular_tight, size)
        for size in ICO_SIZES
    }
    write_ico(ROOT / "app/src/WinShot.App/Assets/winshot.ico", frames)
    resized(regular_tight, 1024).save(ROOT / "app/src/WinShot.App/Assets/winshot-master.png", optimize=True)
    resized(regular_tight, 256).save(ROOT / "website/assets/app-icon.png", optimize=True)
    resized(regular, 1024).save(ROOT / "mac/Icon/Stillmark-flat.png", optimize=True)

    if args.preview:
        args.preview.mkdir(parents=True, exist_ok=True)
        for size, frame in frames.items():
            frame.save(args.preview / f"ico-{size}.png")


if __name__ == "__main__":
    main()
