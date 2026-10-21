"""Generate Exo Launcher's app icon and the installer's pictures from one geometry.

The mark is three sheared bars forming an italic E. All three share a left
edge (that is what reads as an E rather than a Z) and the middle bar is
shortened on the right. Shear is applied globally about the vertical centre so
the mark stays optically balanced instead of leaning out of its box.

The app icon is the mark as Liquid Glass (DESIGN.md › App icon), built the way
Icon Composer builds one: a background and one group of foreground shapes, lit
from above, no thin lines, no sharp corners.

- The plate is a continuous-curvature squircle (Apple's icon mask: the same 45°
  corner inset as a 25% radius, no kink where the curve meets the edge), black
  glass for an AMOLED app: graphite at the top fading to black, a soft sheen
  high in it, a specular line along its top and a fainter catch along its foot.
- The bars are slabs of white glass, each lit from above and softening to a
  cool light gray at its foot, with softened corners, a shadow under them and a
  faint bloom on the black.
- Detail follows size, as a font's optical sizes do: at 32 px and under the
  plate is a plain gradient and the bars are flat white with their top and
  bottom on whole pixels, so the taskbar's 16 to 32 px stay crisp.

    python tools/make-logo.py

Writes ExoLauncher/Assets/ExoLauncher.ico (the app, setup, uninstaller,
shortcuts and Apps & features all use it), the web marks (ui/public/logo.png,
favicon.png, logo.svg, favicon.svg, docs/media/logo.png, brand/exo-mark.svg)
and tools/installer/ui/*.bmp (setup's own screens, one file per Windows scale
so nothing is stretched up).
"""
from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

BOX = 64.0
SHEAR_DEG = 11.0
BAR_H = 9.0
GAP = 5.5
X_LEFT = 14.0
X_RIGHT = 50.0
X_MID_RIGHT = 38.0
SQUIRCLE_N = 4.4
BAR_CORNER = 1.0  # in the 64 box: softened, not round

PLATE = ((0.0, (34, 34, 38)), (0.45, (12, 12, 14)), (1.0, (4, 4, 5)))
PLATE_SMALL = ((0.0, (26, 26, 29)), (1.0, (6, 6, 7)))
BAR_TOP, BAR_MID, BAR_FOOT = (255, 255, 255), (247, 247, 250), (216, 216, 225)

# Setup's screens: DESIGN.md tokens.
PAGE = (0, 0, 0)
INK = (255, 255, 255)
INK_HOVER = (232, 232, 232)
TRACK = (44, 44, 44)
KNOB_OFF = (157, 157, 157)
SCALES = (100, 125, 150, 175, 200, 250, 300)
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

REPO = Path(__file__).resolve().parent.parent


def squircle(size: float, inset: float = 0.0, steps: int = 720) -> list[tuple[float, float]]:
    a, c = size / 2 - inset, size / 2
    pts = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        ct, st = math.cos(t), math.sin(t)
        pts.append((c + a * math.copysign(abs(ct) ** (2 / SQUIRCLE_N), ct), c + a * math.copysign(abs(st) ** (2 / SQUIRCLE_N), st)))
    return pts


def rounded(pts: list[tuple[float, float]], r: float, steps: int = 12) -> list[tuple[float, float]]:
    """A closed polygon whose corners turn through a curve that leaves each edge `r` from the corner."""
    out = []
    for i, v in enumerate(pts):
        p0, p1 = pts[i - 1], pts[(i + 1) % len(pts)]
        la, lb = math.dist(p0, v), math.dist(v, p1)
        k = min(r, la / 2, lb / 2)
        a = (v[0] + (p0[0] - v[0]) * k / la, v[1] + (p0[1] - v[1]) * k / la)
        b = (v[0] + (p1[0] - v[0]) * k / lb, v[1] + (p1[1] - v[1]) * k / lb)
        for j in range(steps + 1):
            t = j / steps
            out.append(((1 - t) ** 2 * a[0] + 2 * (1 - t) * t * v[0] + t * t * b[0], (1 - t) ** 2 * a[1] + 2 * (1 - t) * t * v[1] + t * t * b[1]))
    return out


def bars(size: float, snap: bool) -> list[list[tuple[float, float]]]:
    """Bars in pixels for a `size` box. Small icons put each bar's top and
    bottom on whole pixels so 16-48 px stay crisp instead of gray-edged."""
    tan = math.tan(math.radians(SHEAR_DEG))
    k = size / BOX
    top = (BOX - (3 * BAR_H + 2 * GAP)) / 2

    def offset(y: float) -> float:
        return (size / 2 - y) * tan

    out = []
    for i in range(3):
        y0 = (top + i * (BAR_H + GAP)) * k
        y1 = y0 + BAR_H * k
        if snap:
            y0, y1 = round(y0), round(y1)
        left, right = X_LEFT * k, (X_MID_RIGHT if i == 1 else X_RIGHT) * k
        out.append([(left + offset(y0), y0), (right + offset(y0), y0), (right + offset(y1), y1), (left + offset(y1), y1)])
    return out


def _mask(px: int, polys) -> Image.Image:
    m = Image.new("L", (px, px), 0)
    d = ImageDraw.Draw(m)
    for p in polys:
        d.polygon(p, fill=255)
    return m


def _vgrad(px: int, stops) -> Image.Image:
    """A vertical gradient through (position 0..1, rgb or rgba) stops, px square."""
    col = Image.new("RGBA", (1, px))
    for y in range(px):
        t = y / max(1, px - 1)
        for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
            if t0 <= t <= t1:
                f = (t - t0) / (t1 - t0) if t1 > t0 else 0.0
                c = tuple(round(a + (b - a) * f) for a, b in zip(c0, c1))
                col.putpixel((0, y), c if len(c) == 4 else (*c, 255))
                break
    return col.resize((px, px), Image.NEAREST)


def _over(img: Image.Image, rgb: tuple, alpha: Image.Image) -> Image.Image:
    return Image.composite(Image.new("RGBA", img.size, (*rgb, 255)), img, alpha)


def render(size: int, page: tuple | None = None) -> Image.Image:
    """The icon at `size` px, supersampled so curves and shears stay clean; `page` fills behind it."""
    ss = 4 if size >= 128 else 8
    px = size * ss
    rich = size >= 48
    plate = _mask(px, [squircle(px)])
    img = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    img.paste(_vgrad(px, PLATE if size >= 24 else PLATE_SMALL), (0, 0), plate)
    if rich:
        sheen = Image.new("L", (px, px), 0)
        ImageDraw.Draw(sheen).ellipse([-0.25 * px, -0.62 * px, 1.25 * px, 0.42 * px], fill=30)
        img = _over(img, (255, 255, 255), ImageChops.multiply(sheen.filter(ImageFilter.GaussianBlur(px * 0.08)), plate))

    polys = bars(px, snap=size <= 48)
    if rich:
        polys = [rounded(p, BAR_CORNER * px / BOX) for p in polys]
    bmask = _mask(px, polys)
    if rich:
        bloom = bmask.filter(ImageFilter.GaussianBlur(px * 0.05)).point(lambda v: v * 0.16)
        img = _over(img, (255, 255, 255), ImageChops.multiply(bloom, plate))
        shadow = Image.new("L", (px, px), 0)
        shadow.paste(bmask, (0, round(px * 0.018)))
        shadow = shadow.filter(ImageFilter.GaussianBlur(px * 0.016)).point(lambda v: v * 0.7)
        img = _over(img, (0, 0, 0), ImageChops.multiply(shadow, plate))
        for p in polys:
            y0, y1 = min(y for _, y in p) / px, max(y for _, y in p) / px
            glass = _vgrad(px, [(0.0, BAR_TOP), (y0, BAR_TOP), (y0 + 0.55 * (y1 - y0), BAR_MID), (y1, BAR_FOOT), (1.0, BAR_FOOT)])
            img.paste(glass, (0, 0), _mask(px, [p]))
    else:
        img.paste((*BAR_TOP, 255), (0, 0), bmask)

    if size >= 24:
        width = max(px * 0.012, ss * (1.0 if size < 64 else 1.2))
        ring = ImageChops.subtract(plate, _mask(px, [squircle(px, inset=width)]))
        light = _vgrad(px, [(0.0, (255, 255, 255, 120 if rich else 70)), (0.35, (255, 255, 255, 18)), (0.7, (255, 255, 255, 10)), (1.0, (255, 255, 255, 40 if rich else 20))])
        img = _over(img, (255, 255, 255), ImageChops.multiply(light.getchannel("A"), ring))

    img.putalpha(plate)
    out = img.resize((size, size), Image.LANCZOS)
    if page:
        bg = Image.new("RGBA", (size, size), (*page, 255))
        bg.alpha_composite(out)
        return bg
    return out


def _path(pts) -> str:
    return "M" + " L".join(f"{x:.2f} {y:.2f}" for x, y in pts) + " Z"


def svg(small: bool) -> str:
    """The icon as SVG in the 64 box: the same squircle, bars and light; `small` keeps only what reads at 16 px."""
    plate = _path(squircle(BOX, steps=240))
    polys = bars(BOX, snap=False) if small else [rounded(p, BAR_CORNER) for p in bars(BOX, snap=False)]
    stops = PLATE_SMALL if small else PLATE
    defs = ['<linearGradient id="p" x1="0" y1="0" x2="0" y2="1">' + "".join(f'<stop offset="{t}" stop-color="#{r:02x}{g:02x}{b:02x}"/>' for t, (r, g, b) in stops) + "</linearGradient>"]
    body = [f'<path d="{plate}" fill="url(#p)"/>']
    if not small:
        defs += [
            '<radialGradient id="s" cx="0.5" cy="-0.1" r="0.75"><stop offset="0" stop-color="#fff" stop-opacity="0.12"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></radialGradient>',
            '<linearGradient id="r" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity="0.47"/><stop offset="0.35" stop-color="#fff" stop-opacity="0.07"/><stop offset="0.7" stop-color="#fff" stop-opacity="0.04"/><stop offset="1" stop-color="#fff" stop-opacity="0.16"/></linearGradient>',
            '<filter id="b" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="3.2"/></filter>',
            '<filter id="d" x="-20%" y="-20%" width="140%" height="160%"><feGaussianBlur stdDeviation="1"/></filter>',
            f'<clipPath id="c"><path d="{plate}"/></clipPath>',
        ]
        bars_d = " ".join(_path(p) for p in polys)
        body += [
            f'<path d="{plate}" fill="url(#s)"/>',
            f'<g clip-path="url(#c)"><path d="{bars_d}" fill="#fff" opacity="0.16" filter="url(#b)"/>'
            f'<path d="{bars_d}" fill="#000" opacity="0.7" transform="translate(0 1.15)" filter="url(#d)"/></g>',
        ]
        for i, p in enumerate(polys):
            y0, y1 = min(y for _, y in p), max(y for _, y in p)
            defs.append(f'<linearGradient id="g{i}" gradientUnits="userSpaceOnUse" x1="0" y1="{y0:.2f}" x2="0" y2="{y1:.2f}"><stop offset="0" stop-color="#fff"/><stop offset="0.55" stop-color="#f7f7fa"/><stop offset="1" stop-color="#d8d8e1"/></linearGradient>')
            body.append(f'<path d="{_path(p)}" fill="url(#g{i})"/>')
        body.append(f'<path d="{_path(squircle(BOX, inset=0.38, steps=240))}" fill="none" stroke="url(#r)" stroke-width="0.77"/>')
    else:
        body.append(f'<path d="{" ".join(_path(p) for p in polys)}" fill="#fff"/>')
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><defs>{"".join(defs)}</defs>{"".join(body)}</svg>\n'


def capsule(w: int, h: int, fill: tuple) -> Image.Image:
    ss = 8
    img = Image.new("RGB", (w * ss, h * ss), PAGE)
    ImageDraw.Draw(img).rounded_rectangle([0, 0, w * ss - 1, h * ss - 1], radius=h * ss / 2, fill=fill)
    return img.resize((w, h), Image.LANCZOS)


def switch(scale: float, on: bool) -> Image.Image:
    """Exo's switch: a white track with a black knob when on, the track gray
    with a muted knob when off."""
    w, h = round(36 * scale), round(20 * scale)
    ss = 8
    img = Image.new("RGB", (w * ss, h * ss), PAGE)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, w * ss - 1, h * ss - 1], radius=h * ss / 2, fill=INK if on else TRACK)
    pad = 2 * scale * ss
    knob = h * ss - 2 * pad
    x = (w * ss - pad - knob) if on else pad
    d.ellipse([x, pad, x + knob, pad + knob], fill=PAGE if on else KNOB_OFF)
    return img.resize((w, h), Image.LANCZOS)


def main() -> None:
    frames = [render(s) for s in ICO_SIZES]
    assets = REPO / "ExoLauncher" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    frames[-1].save(assets / "ExoLauncher.ico", format="ICO", sizes=[(s, s) for s in ICO_SIZES], append_images=frames[:-1])
    print(f"icon         {assets / 'ExoLauncher.ico'} ({list(ICO_SIZES)})")

    big, small = frames[-1], frames[ICO_SIZES.index(32)]
    for path, img in ((REPO / "ui" / "public" / "logo.png", big), (REPO / "docs" / "media" / "logo.png", big), (REPO / "ui" / "public" / "favicon.png", small)):
        img.save(path, optimize=True)
        print(f"web          {path}")
    for path, text in ((REPO / "ui" / "public" / "logo.svg", svg(False)), (REPO / "brand" / "exo-mark.svg", svg(False)), (REPO / "ui" / "public" / "favicon.svg", svg(True))):
        path.write_text(text, encoding="utf-8")
        print(f"svg          {path}")

    ui = REPO / "tools" / "installer" / "ui"
    ui.mkdir(parents=True, exist_ok=True)
    for old in ui.glob("*.bmp"):
        old.unlink()
    for s in SCALES:
        k = s / 100
        w, h = round(232 * k), round(44 * k)
        for name, img in (
            ("mark", render(round(72 * k), page=PAGE).convert("RGB")),
            ("button", capsule(w, h, INK)),
            ("button-hover", capsule(w, h, INK_HOVER)),
            ("switch-on", switch(k, True)),
            ("switch-off", switch(k, False)),
        ):
            # A 256-color palette holds them all: a third of the size.
            img.quantize(256).save(ui / f"{name}-{s}.bmp")
    print(f"setup        {ui} (scales {list(SCALES)})")


if __name__ == "__main__":
    main()
