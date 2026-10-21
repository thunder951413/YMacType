#!/usr/bin/env python3
"""Generates control-panel/app.ico: a white geometric "T" on a rounded blue square.

The glyph is drawn from rectangles (no font needed) at 8x and scaled down, so
every size in the .ico is crisp. Run: python3 scripts/make-icon.py
"""
import io
import struct
import sys
from pathlib import Path
from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
TOP = (59, 111, 191)      # #3B6FBF
BOTTOM = (36, 70, 128)    # #244680


def render(size, scale=8):
    s = size * scale
    # Background: vertical gradient clipped to a rounded square.
    bg = Image.new("RGBA", (s, s))
    px = bg.load()
    for y in range(s):
        t = y / (s - 1)
        color = tuple(round(TOP[i] + (BOTTOM[i] - TOP[i]) * t) for i in range(3)) + (255,)
        for x in range(s):
            px[x, y] = color
    mask = Image.new("L", (s, s), 0)
    inset = 0
    radius = round(s * 0.22)
    ImageDraw.Draw(mask).rounded_rectangle(
        [inset, inset, s - 1 - inset, s - 1 - inset], radius=radius, fill=255)
    bg.putalpha(mask)

    # "T": crossbar + stem, optically centred.
    glyph = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(glyph)
    bar_w, bar_h = 0.58, 0.16
    stem_w, stem_h = 0.18, 0.46
    top = 0.22
    left = (1 - bar_w) / 2
    d.rounded_rectangle(
        [left * s, top * s, (left + bar_w) * s, (top + bar_h) * s],
        radius=round(s * 0.025), fill=255)
    stem_left = (1 - stem_w) / 2
    d.rounded_rectangle(
        [stem_left * s, (top + bar_h - 0.01) * s,
         (stem_left + stem_w) * s, (top + bar_h + stem_h) * s],
        radius=round(s * 0.025), fill=255)
    white = Image.new("RGBA", (s, s), (255, 255, 255, 255))
    bg.paste(white, (0, 0), glyph)
    return bg.resize((size, size), Image.LANCZOS)


def dib(image):
    """32-bit BGRA DIB (bottom-up) plus an empty AND mask, as classic ICO frames use."""
    w, h = image.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    rows = []
    pixels = image.convert("RGBA").load()
    for y in range(h - 1, -1, -1):
        rows.append(b"".join(
            struct.pack("<BBBB", pixels[x, y][2], pixels[x, y][1], pixels[x, y][0], pixels[x, y][3])
            for x in range(w)))
    mask_row = ((w + 31) // 32) * 4
    return header + b"".join(rows) + bytes(mask_row * h)


def write_ico(path, images):
    """BMP frames up to 128 px (widest compatibility), PNG for 256 px."""
    frames = []
    for image in images:
        if image.width >= 256:
            buffer = io.BytesIO()
            image.save(buffer, format="PNG")
            frames.append(buffer.getvalue())
        else:
            frames.append(dib(image))
    data = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for image, frame in zip(images, frames):
        size = 0 if image.width >= 256 else image.width
        data += struct.pack("<BBBBHHII", size, size, 0, 0, 1, 32, len(frame), offset)
        offset += len(frame)
    path.write_bytes(data + b"".join(frames))


def main():
    root = Path(__file__).resolve().parent.parent
    images = [render(size) for size in SIZES]
    out = root / "control-panel" / "app.ico"
    write_ico(out, images)
    print("wrote", out)
    if len(sys.argv) > 1:
        sheet = Image.new("RGBA", (sum(SIZES) + 20 * len(SIZES), 280), (240, 241, 244, 255))
        x = 10
        for img in images:
            sheet.paste(img, (x, 10), img)
            x += img.width + 20
        # Same icons on a dark taskbar-like strip, drawn at 1x for the small sizes.
        dark = Image.new("RGBA", (sheet.width, 70), (32, 32, 32, 255))
        x = 10
        for img in images[:5]:
            dark.paste(img, (x, 20), img)
            x += img.width + 20
        sheet.paste(dark, (0, 210))
        sheet.save(sys.argv[1])
        print("preview", sys.argv[1])


main()
