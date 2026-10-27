"""Builds the app's icon and logo from the brand sources in docs/design/brand.

    python -I tools/brand/build_logo_assets.py docs/design/brand src/Pennyworth.App/Assets [tools/package/linux/icons]

Needs Pillow. Writes:
  pennyworth.ico  Windows icon (16-256 px) from logo-with-background.webp
  logo.png        512 px transparent logo from logo-transparent.svg
  hicolor/NxN/apps/pennyworth.png  the Linux launcher icon at 16-512 px, from the same webp, when a
                  third argument names the folder (it is packaged under share/icons by tools/package)

The SVG holds no vector paths: it is two embedded images (ring, tuxedo), each cut out by a
luminance mask image. This script reproduces that composition with Pillow.
"""
import base64
import io
import os
import re
import sys

from PIL import Image, ImageChops, ImageFilter

SCALE = 2.0  # render scale over the SVG's user units (672.75 x 800.25)


def layer(canvas, color, mask, sx, sy, tx, ty, clip):
    # Luminance mask; drop the faint compression noise so the background stays fully clear.
    mask = mask.point(lambda v: 0 if v < 24 else min(255, round((v - 24) * 255 / 200)))
    w, h = round(color.width * sx * SCALE), round(color.height * sy * SCALE)
    color, mask = color.resize((w, h), Image.LANCZOS), mask.resize((w, h), Image.LANCZOS)

    # Pixels near the mask edge still carry the old black background (a dark halo on light
    # themes), so grow the colours of the solid interior outward over them.
    known = mask.filter(ImageFilter.MinFilter(11)).point(lambda v: 255 if v >= 250 else 0)
    for _ in range(12):
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            fill = ImageChops.subtract(ImageChops.offset(known, dx, dy), known)
            color = Image.composite(ImageChops.offset(color, dx, dy), color, fill)
            known = ImageChops.lighter(known, fill)

    rgba = color.convert('RGBA')
    rgba.putalpha(mask)
    piece = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    piece.paste(rgba, (round(tx * SCALE), round(ty * SCALE)))
    x0, y0, x1, y1 = (round(v * SCALE) for v in clip)
    clipped = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    clipped.paste(piece.crop((x0, y0, x1, y1)), (x0, y0))
    canvas.alpha_composite(clipped)


def build_logo(svg_path, out_path):
    svg = open(svg_path, encoding='utf-8').read()
    ring_mask, tux_mask, ring, tux = (
        Image.open(io.BytesIO(base64.b64decode(data)))
        for data in re.findall(r'data:image/png;base64,([A-Za-z0-9+/=]+)', svg))

    canvas = Image.new('RGBA', (round(672.75 * SCALE), round(800.25 * SCALE)), (0, 0, 0, 0))
    # Transforms and clip rectangles copied from the SVG.
    layer(canvas, ring.convert('RGB'), ring_mask.convert('L'),
          0.749298, 0.749298, -1.308512, -4.495786, (0.191406, 0, 672.308594, 684.859375))
    layer(canvas, tux.convert('RGB'), tux_mask.convert('L'),
          1.163833, 1.164434, 30.320974, 36.383667, (44.289062, 45.695312, 627.242188, 798.738281))

    logo = canvas.crop(canvas.getbbox())
    side = max(logo.size)
    square = Image.new('RGBA', (side, side), (0, 0, 0, 0))
    square.paste(logo, ((side - logo.width) // 2, (side - logo.height) // 2))
    square.resize((512, 512), Image.LANCZOS).save(out_path, optimize=True)


def trimmed_icon(webp_path):
    # Trim most of the black margin so the ring fills small taskbar sizes.
    return Image.open(webp_path).convert('RGBA').crop((110, 110, 1090, 1090))


def build_hicolor(webp_path, out_dir):
    icon = trimmed_icon(webp_path)
    for size in (16, 24, 32, 48, 64, 128, 256, 512):
        folder = f'{out_dir}/hicolor/{size}x{size}/apps'
        os.makedirs(folder, exist_ok=True)
        icon.resize((size, size), Image.LANCZOS).save(f'{folder}/pennyworth.png', optimize=True)


def build_icon(webp_path, out_path):
    icon = trimmed_icon(webp_path)
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    icon.resize((256, 256), Image.LANCZOS).save(out_path, sizes=sizes)


if __name__ == '__main__':
    source, out = sys.argv[1], sys.argv[2]
    build_logo(f'{source}/logo-transparent.svg', f'{out}/logo.png')
    build_icon(f'{source}/logo-with-background.webp', f'{out}/pennyworth.ico')
    print('wrote', f'{out}/logo.png', f'{out}/pennyworth.ico')
    if len(sys.argv) > 3:
        build_hicolor(f'{source}/logo-with-background.webp', sys.argv[3])
        print('wrote', f'{sys.argv[3]}/hicolor')
