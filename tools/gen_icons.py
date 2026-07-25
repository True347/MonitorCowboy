#!/usr/bin/env python3
"""Generate the plugin icons into Images/.

Flat style: rounded-square colored background, white geometric pictogram,
transparent corners. Rendered at 4x and downscaled with LANCZOS.

Usage: python tools/gen_icons.py
"""

from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 256
SS = 4
CANVAS = SIZE * SS
CORNER_RADIUS = 0.22
WHITE = (255, 255, 255, 255)

OUT_DIR = Path(__file__).resolve().parent.parent / "Images"


def s(value):
    """Scale a 256-space coordinate to the supersampled canvas."""
    return round(value * SS)


def sbox(x0, y0, x1, y1):
    return [s(x0), s(y0), s(x1), s(y1)]


def new_icon(color):
    img = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.rounded_rectangle(
        [0, 0, CANVAS - 1, CANVAS - 1], radius=round(CANVAS * CORNER_RADIUS), fill=color
    )
    return img, draw


def capped_line(draw, x0, y0, x1, y1, width, fill):
    """Line with round end caps."""
    draw.line([s(x0), s(y0), s(x1), s(y1)], fill=fill, width=s(width))
    r = width / 2
    for cx, cy in ((x0, y0), (x1, y1)):
        draw.ellipse(sbox(cx - r, cy - r, cx + r, cy + r), fill=fill)


def rounded_polygon(draw, points, radius, fill):
    """Filled polygon with rounded corners (fill + thick round-joint outline)."""
    scaled = [(s(x), s(y)) for x, y in points]
    draw.polygon(scaled, fill=fill)
    draw.line(scaled + [scaled[0], scaled[1]], fill=fill, width=s(radius * 2), joint="curve")


def draw_monitor(draw, accent):
    del accent
    draw.rounded_rectangle(sbox(44, 56, 212, 170), radius=s(14), outline=WHITE, width=s(16))
    draw.rectangle(sbox(116, 170, 140, 196), fill=WHITE)
    draw.rounded_rectangle(sbox(80, 196, 176, 212), radius=s(8), fill=WHITE)


def draw_input(draw, accent):
    # Port bracket on the right, open toward the incoming arrow.
    draw.rounded_rectangle(sbox(124, 72, 212, 184), radius=s(16), outline=WHITE, width=s(16))
    draw.rectangle(sbox(116, 98, 148, 158), fill=accent)
    draw.rounded_rectangle(sbox(40, 118, 128, 138), radius=s(10), fill=WHITE)
    draw.polygon([(s(122), s(100)), (s(168), s(128)), (s(122), s(156))], fill=WHITE)


def draw_volume(draw, accent):
    del accent
    rounded_polygon(
        draw,
        [(56, 106), (92, 106), (132, 68), (132, 188), (92, 150), (56, 150)],
        radius=5,
        fill=WHITE,
    )
    for radius in (44, 74):
        draw.arc(
            sbox(140 - radius, 128 - radius, 140 + radius, 128 + radius),
            start=-52,
            end=52,
            fill=WHITE,
            width=s(15),
        )


def draw_back(draw, accent):
    del accent
    draw.rounded_rectangle(sbox(112, 112, 210, 144), radius=s(14), fill=WHITE)
    rounded_polygon(draw, [(52, 128), (126, 74), (126, 182)], radius=8, fill=WHITE)


def draw_warning(draw, accent):
    rounded_polygon(draw, [(128, 56), (52, 192), (204, 192)], radius=12, fill=WHITE)
    draw.rounded_rectangle(sbox(118, 100, 138, 152), radius=s(10), fill=accent)
    draw.ellipse(sbox(117, 162, 139, 184), fill=accent)


def draw_error(draw, accent):
    draw.ellipse(sbox(52, 52, 204, 204), fill=WHITE)
    capped_line(draw, 98, 98, 158, 158, width=24, fill=accent)
    capped_line(draw, 98, 158, 158, 98, width=24, fill=accent)


def draw_app(draw, accent):
    del accent
    # Monitor body.
    draw.rounded_rectangle(sbox(48, 108, 208, 198), radius=s(12), outline=WHITE, width=s(15))
    draw.rectangle(sbox(120, 198, 136, 214), fill=WHITE)
    draw.rounded_rectangle(sbox(88, 214, 168, 228), radius=s(7), fill=WHITE)
    # Cowboy hat: dome crown over a wide flat brim, floating just above the bezel.
    draw.rounded_rectangle(
        sbox(92, 30, 164, 82), radius=s(24), corners=(True, True, False, False), fill=WHITE
    )
    draw.rounded_rectangle(sbox(54, 76, 202, 98), radius=s(11), fill=WHITE)


ICONS = [
    ("app.png", (0xB7, 0x79, 0x1F, 0xFF), draw_app),
    ("monitor.png", (0x2B, 0x6C, 0xB0, 0xFF), draw_monitor),
    ("input.png", (0x6B, 0x46, 0xC1, 0xFF), draw_input),
    ("volume.png", (0x2F, 0x85, 0x5A, 0xFF), draw_volume),
    ("back.png", (0x4A, 0x55, 0x68, 0xFF), draw_back),
    ("warning.png", (0xC0, 0x56, 0x21, 0xFF), draw_warning),
    ("error.png", (0xC5, 0x30, 0x30, 0xFF), draw_error),
]


def main():
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    for name, color, painter in ICONS:
        img, draw = new_icon(color)
        painter(draw, color)
        img.resize((SIZE, SIZE), Image.LANCZOS).save(OUT_DIR / name)
        print(f"wrote {OUT_DIR / name}")


if __name__ == "__main__":
    main()
