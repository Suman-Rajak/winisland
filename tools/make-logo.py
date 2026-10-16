"""
Glyde's logo: a glossy gradient orb with a face (two capsule eyes, a smile), like an emoji.

Writes, from one set of shapes:
  src/WinIsland/Assets/Glyde.svg              the logo as a vector
  src/WinIsland/Assets/island.ico             the app and tray icon (16 to 256 px)
  src/WinIsland/Assets/Square44x44Logo.png    the installed app's icons and tiles
  src/WinIsland/Assets/StoreLogo.png
  src/WinIsland/Assets/Square150x150Logo.png
  src/WinIsland/Assets/Wide310x150Logo.png    (the face and the name)

The app draws the same face live (GlydeFace.xaml), on the same 256 x 256 grid, so keep the two in step.
Run from the repository root:  python tools/make-logo.py   (needs Pillow and numpy)
"""

import io
import os
import struct

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "src", "WinIsland", "Assets")

# ---------------------------------------------------------------- the shapes, on a 256 x 256 grid

CENTRE, RADIUS = 128, 118
GRADIENT = [(0.0, (0x6E, 0x7B, 0xFF)), (0.45, (0xA6, 0x6C, 0xFF)), (1.0, (0xF0, 0x6A, 0xB4))]  # periwinkle, violet, pink
GRADIENT_FROM, GRADIENT_TO = (40, 30), (216, 226)  # top left to bottom right

EYE_WIDTH, EYE_HEIGHT = 28, 50
EYES = [(94, 112), (162, 112)]  # centres
SMILE = [(90, 160), (128, 194), (166, 160)]  # quadratic Bézier: start, control, end
SMILE_WIDTH = 14

# Small icons get bolder features (and no gloss), so the face still reads at 16 px.
BOLD = dict(eye_width=36, eye_height=60, eyes=[(90, 108), (166, 108)], smile_width=22,
            smile=[(84, 160), (128, 200), (172, 160)])


def svg():
    (x0, y0), (x1, y1) = GRADIENT_FROM, GRADIENT_TO
    stops = "".join(f'<stop offset="{o}" stop-color="#{r:02X}{g:02X}{b:02X}"/>' for o, (r, g, b) in GRADIENT)
    eyes = "".join(
        f'<rect x="{x - EYE_WIDTH / 2}" y="{y - EYE_HEIGHT / 2}" width="{EYE_WIDTH}" height="{EYE_HEIGHT}" rx="{EYE_WIDTH / 2}" fill="#fff"/>'
        for x, y in EYES)
    (sx, sy), (cx, cy), (ex, ey) = SMILE
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <title>Glyde</title>
  <defs>
    <linearGradient id="body" gradientUnits="userSpaceOnUse" x1="{x0}" y1="{y0}" x2="{x1}" y2="{y1}">{stops}</linearGradient>
    <radialGradient id="depth" cx="128" cy="96" r="150" gradientUnits="userSpaceOnUse">
      <stop offset="0.55" stop-color="#1E0F46" stop-opacity="0"/><stop offset="1" stop-color="#1E0F46" stop-opacity="0.32"/>
    </radialGradient>
    <radialGradient id="gloss" cx="96" cy="64" r="70" gradientUnits="userSpaceOnUse" gradientTransform="translate(96 64) rotate(-28) scale(1 0.55) translate(-96 -64)">
      <stop offset="0" stop-color="#fff" stop-opacity="0.6"/><stop offset="1" stop-color="#fff" stop-opacity="0"/>
    </radialGradient>
  </defs>
  <circle cx="{CENTRE}" cy="{CENTRE}" r="{RADIUS}" fill="url(#body)"/>
  <circle cx="{CENTRE}" cy="{CENTRE}" r="{RADIUS}" fill="url(#depth)"/>
  <ellipse cx="96" cy="64" rx="70" ry="38.5" transform="rotate(-28 96 64)" fill="url(#gloss)"/>
  <circle cx="{CENTRE}" cy="{CENTRE}" r="{RADIUS - 1.5}" fill="none" stroke="#fff" stroke-opacity="0.22" stroke-width="3"/>
  {eyes}
  <path d="M{sx} {sy} Q{cx} {cy} {ex} {ey}" fill="none" stroke="#fff" stroke-width="{SMILE_WIDTH}" stroke-linecap="round"/>
</svg>
"""


def face(size, bold=None):
    """The logo as an RGBA image of size x size pixels, drawn large and scaled down for smooth edges."""
    bold = size <= 32 if bold is None else bold
    s = 8 if size <= 64 else 4
    n = 256 * s
    yy, xx = (np.mgrid[0:n, 0:n] + 0.5) / s

    # The orb: the gradient, darkening a little towards the edge for depth.
    (x0, y0), (x1, y1) = GRADIENT_FROM, GRADIENT_TO
    d = np.array([x1 - x0, y1 - y0], dtype=float)
    t = np.clip(((xx - x0) * d[0] + (yy - y0) * d[1]) / (d @ d), 0, 1)
    rgb = np.zeros((n, n, 3))
    for (o0, c0), (o1, c1) in zip(GRADIENT, GRADIENT[1:]):
        seg = (t >= o0) & (t <= o1)
        f = ((t - o0) / (o1 - o0))[..., None]
        rgb[seg] = (np.array(c0) * (1 - f) + np.array(c1) * f)[seg]
    edge = np.clip((np.hypot(xx - 128, yy - 96) / 150 - 0.55) / 0.45, 0, 1)[..., None]
    rgb = rgb * (1 - 0.32 * edge) + np.array([0x1E, 0x0F, 0x46]) * 0.32 * edge
    alpha = (np.hypot(xx - CENTRE, yy - CENTRE) <= RADIUS).astype(float)

    if not bold:
        # Gloss: light catching the top left, and a faint bright rim.
        u, v = xx - 96, yy - 64
        c, sn = np.cos(np.radians(28)), np.sin(np.radians(28))
        gx, gy = (u * c - v * sn) / 70, (u * sn + v * c) / 38.5
        gloss = np.clip(1 - np.hypot(gx, gy), 0, 1)[..., None] * 0.6
        rgb = rgb * (1 - gloss) + 255 * gloss
        rim = (np.abs(np.hypot(xx - CENTRE, yy - CENTRE) - (RADIUS - 1.5)) <= 1.5)[..., None] * 0.22
        rgb = rgb * (1 - rim) + 255 * rim

    image = Image.fromarray(np.dstack([rgb, alpha * 255]).astype(np.uint8), "RGBA")

    # The features, in white, with a soft shadow under them.
    eyes, ew, eh = (BOLD["eyes"], BOLD["eye_width"], BOLD["eye_height"]) if bold else (EYES, EYE_WIDTH, EYE_HEIGHT)
    smile, sw = (BOLD["smile"], BOLD["smile_width"]) if bold else (SMILE, SMILE_WIDTH)
    features = Image.new("L", (n, n), 0)
    draw = ImageDraw.Draw(features)
    for x, y in eyes:
        draw.rounded_rectangle([(x - ew / 2) * s, (y - eh / 2) * s, (x + ew / 2) * s, (y + eh / 2) * s], radius=ew / 2 * s, fill=255)
    (sx, sy), (cx, cy), (ex, ey) = smile
    for k in range(0, 401):
        q = k / 400
        px = (1 - q) ** 2 * sx + 2 * (1 - q) * q * cx + q ** 2 * ex
        py = (1 - q) ** 2 * sy + 2 * (1 - q) * q * cy + q ** 2 * ey
        r = sw / 2 * s
        draw.ellipse([px * s - r, py * s - r, px * s + r, py * s + r], fill=255)

    if not bold:
        shadow = features.filter(ImageFilter.GaussianBlur(3 * s)).point(lambda a: a * 0.22)
        dark = Image.new("RGBA", (n, n), (0x1E, 0x0F, 0x46, 0))
        dark.putalpha(Image.fromarray(np.minimum(np.array(shadow), np.array(image.getchannel("A")))))
        image.alpha_composite(dark, (0, 3 * s))
    white = Image.new("RGBA", (n, n), (255, 255, 255, 0))
    white.putalpha(features)
    image.alpha_composite(white)

    return image.resize((size, size), Image.LANCZOS)


def on_plate(width, height, face_size, wordmark=False):
    """The face centred on a transparent tile (with the name beside it on wide tiles)."""
    plate = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    if not wordmark:
        plate.alpha_composite(face(face_size), ((width - face_size) // 2, (height - face_size) // 2))
        return plate

    font = ImageFont.truetype(os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "seguisb.ttf"), int(face_size * 0.62))
    text_width = font.getlength("Glyde")
    gap = face_size * 0.22
    left = int((width - face_size - gap - text_width) / 2)
    plate.alpha_composite(face(face_size), (left, (height - face_size) // 2))
    ImageDraw.Draw(plate).text((left + face_size + gap, height / 2), "Glyde", font=font, fill=(255, 255, 255, 255), anchor="lm")
    return plate


def ico(path, sizes):
    """A Windows .ico with a PNG for each size (Vista and later read those)."""
    pngs = []
    for size in sizes:
        buffer = io.BytesIO()
        face(size).save(buffer, "PNG")
        pngs.append(buffer.getvalue())
    offset = 6 + 16 * len(sizes)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(sizes)))
        for size, png in zip(sizes, pngs):
            f.write(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(png), offset))
            offset += len(png)
        for png in pngs:
            f.write(png)


if __name__ == "__main__":
    with open(os.path.join(ASSETS, "Glyde.svg"), "w", encoding="utf-8", newline="\n") as f:
        f.write(svg())
    ico(os.path.join(ASSETS, "island.ico"), [16, 20, 24, 32, 40, 48, 64, 256])
    on_plate(44, 44, 40).save(os.path.join(ASSETS, "Square44x44Logo.png"))
    on_plate(50, 50, 46).save(os.path.join(ASSETS, "StoreLogo.png"))
    on_plate(150, 150, 104).save(os.path.join(ASSETS, "Square150x150Logo.png"))
    on_plate(310, 150, 96, wordmark=True).save(os.path.join(ASSETS, "Wide310x150Logo.png"))
    print("Wrote Glyde.svg, island.ico and the tile images to", ASSETS)
