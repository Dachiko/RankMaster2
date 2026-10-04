"""Rank Master's two-pane icon (house style, plan H § 3.5): two upright panes on a paper tile with
an ink outline, the left pane filled. Writes a multi-size .ico. Each size is drawn 8x and scaled
down, so the small sizes stay crisp.

    python pc/tools/make-pane-icon.py --out pc/src/RankMaster2.Pc/App/icon.ico
"""
import argparse
from PIL import Image, ImageDraw

BG, INK = (252, 252, 250, 255), (20, 20, 20, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def draw(size):
    k = 8
    s = size * k
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = s / 64  # design grid: 64 units, as the mockup's 64 px tile
    line = max(1.5 * u, k * 1.0)
    d.rounded_rectangle([0.75 * u, 0.75 * u, s - 0.75 * u, s - 0.75 * u], radius=14 * u, fill=BG, outline=INK, width=round(line))
    pane = max(2 * u, k * 1.0)
    top, bottom = 13 * u, s - 13 * u
    lx0, lx1 = 11 * u, 29 * u
    rx0, rx1 = 35 * u, 53 * u
    d.rounded_rectangle([lx0, top, lx1, bottom], radius=3 * u, fill=INK)
    d.rounded_rectangle([rx0, top, rx1, bottom], radius=3 * u, outline=INK, width=round(pane))
    return img.resize((size, size), Image.LANCZOS)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--out", required=True)
    a = p.parse_args()
    big = draw(256)
    big.save(a.out, format="ICO", sizes=[(n, n) for n in SIZES], append_images=[draw(n) for n in SIZES if n != 256])
    print("wrote", a.out)
