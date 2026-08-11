"""Generate the Exo Launcher mark from one geometry definition.

The logo is three sheared bars forming an italic E. All three share a left
edge — that is what reads as an E rather than a Z — and the middle bar is
shortened on the right. Shear is applied globally about the vertical centre so
the mark stays optically balanced instead of leaning out of its box.

    python tools/make-logo.py

Writes ui/public/{logo.png,favicon.png,favicon.svg} and
ExoLauncher/Assets/ExoLauncher.ico. Vite copies ui/public into wwwroot.
"""
from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw

BOX = 64.0
CORNER = 16.0
BG = (5, 5, 5, 255)
FG = (242, 242, 242, 255)

SHEAR_DEG = 11.0
BAR_H = 9.0
GAP = 5.5
X_LEFT = 14.0
X_RIGHT = 50.0
X_MID_RIGHT = 38.0

REPO = Path(__file__).resolve().parent.parent


def _bars() -> list[list[tuple[float, float]]]:
    tan = math.tan(math.radians(SHEAR_DEG))
    centre_y = BOX / 2
    total_h = 3 * BAR_H + 2 * GAP
    top = (BOX - total_h) / 2

    def offset(y: float) -> float:
        return (centre_y - y) * tan

    out = []
    for i in range(3):
        y0 = top + i * (BAR_H + GAP)
        y1 = y0 + BAR_H
        right = X_MID_RIGHT if i == 1 else X_RIGHT
        out.append([
            (X_LEFT + offset(y0), y0),
            (right + offset(y0), y0),
            (right + offset(y1), y1),
            (X_LEFT + offset(y1), y1),
        ])
    return out


def render(size: int) -> Image.Image:
    """Supersample x4 so the sheared edges stay clean at icon sizes."""
    ss = 4
    px = size * ss
    scale = px / BOX
    img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.rounded_rectangle([0, 0, px - 1, px - 1], radius=CORNER * scale, fill=BG)
    for bar in _bars():
        draw.polygon([(x * scale, y * scale) for x, y in bar], fill=FG)
    return img.resize((size, size), Image.LANCZOS)


def svg() -> str:
    paths = []
    for bar in _bars():
        pts = " ".join(f"{x:.2f},{y:.2f}" for x, y in bar)
        paths.append(f'<polygon points="{pts}" fill="#F2F2F2"/>')
    body = "".join(paths)
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {BOX:.0f} {BOX:.0f}">'
        f'<rect width="{BOX:.0f}" height="{BOX:.0f}" rx="{CORNER:.0f}" fill="#050505"/>'
        f"{body}</svg>\n"
    )


def main() -> None:
    public = REPO / "ui" / "public"
    public.mkdir(parents=True, exist_ok=True)

    render(256).save(public / "logo.png")
    render(32).save(public / "favicon.png")
    (public / "favicon.svg").write_text(svg(), encoding="utf-8")

    ico_sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = [render(s) for s in ico_sizes]
    assets = REPO / "ExoLauncher" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    frames[-1].save(
        assets / "ExoLauncher.ico",
        format="ICO",
        sizes=[(s, s) for s in ico_sizes],
    )

    print(f"logo.png     {public / 'logo.png'}")
    print(f"favicon.png  {public / 'favicon.png'}")
    print(f"favicon.svg  {public / 'favicon.svg'}")
    print(f"icon         {assets / 'ExoLauncher.ico'} ({ico_sizes})")


if __name__ == "__main__":
    main()
