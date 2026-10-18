#!/usr/bin/env python3
"""Build WhatsAppLegacyIcons.ttf from scripts/icons/whatsapp-legacy PNGs.

Codepoints (Private Use Area):
  EB01  search     (appbar-search.png)
  EB02  add        (appbar-add.png)
  EB03  attach     (composer-attach.png)
  EB04  send       (composer-send.png)
  EB05  mic        (composer-mic.png)
  EB06  newchat    (appbar-newchat.png)     speech bubble
  EB07  pin        (appbar-pin.png)         pin conversation
  EB08  pinstart   (appbar-pin-start.png)   pin to Start menu
  EB09  play       (media-play.png)
  EB0A  pause      (media-pause.png)
  EB0B  download   (media-download.png)
  EB0C  stop       (media-stop.png)
  EB0D  flash      (cam-flash.png)          flash on
  EB0E  flashoff   (cam-flash-off.png)      flash off
  EB0F  flashauto  (cam-flash-auto.png)     flash auto (reserved; not on chrome yet)

Ink is read from alpha only (source colour is ignored; XAML sets Foreground).
Each glyph is cropped to its own ink and fitted into the same square box,
centred, so every icon renders at the same optical size for a given FontSize.

Requires: pip install fonttools pillow numpy scikit-image
"""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from skimage import measure

REPO = Path(__file__).resolve().parents[2]
ICONS = Path(__file__).resolve().parent / "whatsapp-legacy"
FONT_PATH = REPO / "src" / "Unison.Uwp" / "Assets" / "Fonts" / "WhatsAppLegacyIcons.ttf"
UPM = 1000
# Ink box edge as a fraction of the em; the rest is equal padding on every side.
INK_BOX = 0.84
# Max deviation (source px) when simplifying traced outlines.
SIMPLIFY_TOLERANCE = 0.35

# name → (codepoint, png filename, ink box scale relative to INK_BOX)
GLYPHS = [
    ("search", 0xEB01, "appbar-search.png", 0.86),
    ("add", 0xEB02, "appbar-add.png", 0.80),
    ("attach", 0xEB03, "composer-attach.png", 1.0),
    ("send", 0xEB04, "composer-send.png", 1.0),
    ("mic", 0xEB05, "composer-mic.png", 1.0),
    ("newchat", 0xEB06, "appbar-newchat.png", 0.90),
    ("pin", 0xEB07, "appbar-pin.png", 0.90),
    ("pinstart", 0xEB08, "appbar-pin-start.png", 0.92),
    ("play", 0xEB09, "media-play.png", 0.88),
    ("pause", 0xEB0A, "media-pause.png", 0.88),
    ("download", 0xEB0B, "media-download.png", 0.90),
    ("stop", 0xEB0C, "media-stop.png", 0.80),
    ("flash", 0xEB0D, "cam-flash.png", 0.90),
    ("flashoff", 0xEB0E, "cam-flash-off.png", 0.90),
    ("flashauto", 0xEB0F, "cam-flash-auto.png", 0.90),
]


def _alpha(path: Path) -> np.ndarray:
    im = Image.open(path).convert("RGBA")
    return np.asarray(im, dtype=np.float32)[:, :, 3] / 255.0


def _signed_area(points: list[tuple[float, float]]) -> float:
    total = 0.0
    for i, (x0, y0) in enumerate(points):
        x1, y1 = points[(i + 1) % len(points)]
        total += x0 * y1 - x1 * y0
    return total / 2.0


def _contains(polygon: list[tuple[float, float]], point: tuple[float, float]) -> bool:
    x, y = point
    inside = False
    j = len(polygon) - 1
    for i in range(len(polygon)):
        xi, yi = polygon[i]
        xj, yj = polygon[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            inside = not inside
        j = i
    return inside


def glyph_from_png(path: Path, scale_factor: float):
    alpha = _alpha(path)
    # Pad so contours touching the image edge still close.
    alpha = np.pad(alpha, 1)
    ys, xs = np.nonzero(alpha > 0.5)
    if xs.size == 0:
        raise RuntimeError(f"No ink found in {path.name}")

    raw = measure.find_contours(alpha, 0.5)
    raw = [measure.approximate_polygon(c, SIMPLIFY_TOLERANCE) for c in raw]
    raw = [c for c in raw if len(c) >= 4]

    # Ink bounds from the traced outlines (sub-pixel), then fit into the box.
    all_pts = np.vstack(raw)
    min_y, min_x = all_pts.min(axis=0)
    max_y, max_x = all_pts.max(axis=0)
    ink_w = max_x - min_x
    ink_h = max_y - min_y
    box = UPM * INK_BOX * scale_factor
    scale = box / max(ink_w, ink_h)
    ox = (UPM - ink_w * scale) / 2.0 - min_x * scale
    oy = (UPM - ink_h * scale) / 2.0 - min_y * scale

    polygons = []
    for contour in raw:
        pts = [(ox + x * scale, UPM - (oy + y * scale)) for y, x in contour[:-1]]
        if abs(_signed_area(pts)) < (scale * scale) * 2.0:
            continue
        polygons.append(pts)

    pen = TTGlyphPen(None)
    for index, pts in enumerate(polygons):
        depth = sum(
            1 for other_index, other in enumerate(polygons)
            if other_index != index and _contains(other, pts[0])
        )
        # TrueType: outer contours clockwise, holes counter-clockwise (y up).
        clockwise = _signed_area(pts) < 0
        if (depth % 2 == 0) != clockwise:
            pts = list(reversed(pts))
        pen.moveTo((round(pts[0][0]), round(pts[0][1])))
        for x, y in pts[1:]:
            pen.lineTo((round(x), round(y)))
        pen.closePath()

    return pen.glyph()


def main() -> int:
    glyph_order = [".notdef"]
    glyphs = {".notdef": TTGlyphPen(None).glyph()}
    cmap = {}
    advance = {".notdef": (UPM, 0)}

    for name, code, filename, scale_factor in GLYPHS:
        png = ICONS / filename
        if not png.is_file():
            print(f"Missing icon: {png}", file=sys.stderr)
            return 1
        glyph = glyph_from_png(png, scale_factor)
        glyph.recalcBounds(None)
        glyphs[name] = glyph
        glyph_order.append(name)
        cmap[code] = name
        # lsb must equal xMin: TrueType rasterizers place the outline from the hmtx lsb,
        # so lsb=0 shifts every glyph left by its own xMin.
        advance[name] = (UPM, glyph.xMin)
        print(f"  {code:04X} {name} <- {filename}")

    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics(advance)
    # Line box == em box (glyphs are centred inside 0..UPM), so FontIcon centres them.
    fb.setupHorizontalHeader(ascent=UPM, descent=0, lineGap=0)
    # Cap height == em too: TextLineBounds="Tight" sizes the TextBlock baseline..capHeight,
    # and a 0 cap height collapses that box and pushes the ink above it.
    fb.setupOS2(
        version=4,
        sTypoAscender=UPM,
        sTypoDescender=0,
        sTypoLineGap=0,
        usWinAscent=UPM,
        usWinDescent=0,
        sCapHeight=UPM,
        sxHeight=UPM,
        fsSelection=0x00C0,  # REGULAR | USE_TYPO_METRICS
    )
    fb.setupPost()
    fb.setupNameTable(
        {
            "familyName": "WhatsAppLegacyIcons",
            "styleName": "Regular",
            "uniqueFontIdentifier": "WhatsAppLegacyIcons",
            "fullName": "WhatsAppLegacyIcons",
            "version": "Version 2.0",
            "psName": "WhatsAppLegacyIcons",
            "description": "WhatsApp Legacy (WP8) UI icons. Regenerate via scripts/icons/generate-whatsapp-legacy-icons.ps1",
        }
    )
    fb.setupHead(unitsPerEm=UPM)

    FONT_PATH.parent.mkdir(parents=True, exist_ok=True)
    fb.save(str(FONT_PATH))
    print(f"Wrote {FONT_PATH}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
