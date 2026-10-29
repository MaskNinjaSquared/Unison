#!/usr/bin/env python3
"""Rebuild UnisonIcons.ttf: filter/new-chat/checks + emoji-category jump icons.

Codepoints (Private Use Area):
  EA01  filter
  EA02  new-chat
  EA03  check (single tick — sent / pending)
  EA04  double-check (delivered / read; color via Foreground)
  EA05  emoji-sports   (scripts/icons/unison-emoji/emoji-sports.png)
  EA06  emoji-nature   (scripts/icons/unison-emoji/emoji-nature.png)
  EA07  emoji-food     (scripts/icons/unison-emoji/emoji-food.png)
  EA08  emoji-symbols  (scripts/icons/unison-emoji/emoji-symbols.png)

Ink is read from alpha only. Regen: scripts/icons/generate-unison-icons.ps1

Requires: pip install fonttools pillow numpy scikit-image
"""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont
from skimage import measure

REPO = Path(__file__).resolve().parents[2]
FONT_PATH = REPO / "src" / "Unison.Uwp" / "Assets" / "Fonts" / "UnisonIcons.ttf"
EMOJI_ICONS = Path(__file__).resolve().parent / "unison-emoji"
UPM = 1000
INK_BOX = 0.84
SIMPLIFY_TOLERANCE = 0.35

# PNG-traced jump icons for the sticker-box SemanticZoom (Segoe Fluent lacks these).
PNG_GLYPHS = [
    ("emoji-sports", 0xEA05, "emoji-sports.png", 0.92),
    ("emoji-nature", 0xEA06, "emoji-nature.png", 0.92),
    ("emoji-food", 0xEA07, "emoji-food.png", 0.92),
    ("emoji-symbols", 0xEA08, "emoji-symbols.png", 0.92),
]


def copy_glyph(gs, name):
    pen = TTGlyphPen(None)
    gs[name].draw(pen)
    return pen.glyph()


def _svg_to_font(x: float, y: float, size: float = 24.0):
    s = UPM / size
    return (x * s, (size - y) * s)


def glyph_check():
    pen = TTGlyphPen(None)
    pts = [
        _svg_to_font(9, 16.2),
        _svg_to_font(4.8, 12),
        _svg_to_font(3.4, 13.4),
        _svg_to_font(9, 19),
        _svg_to_font(21, 7),
        _svg_to_font(19.6, 5.6),
    ]
    pen.moveTo(pts[0])
    for p in pts[1:]:
        pen.lineTo(p)
    pen.closePath()
    return pen.glyph()


def glyph_double_check():
    pen = TTGlyphPen(None)

    def path(coords):
        pts = [_svg_to_font(x, y) for x, y in coords]
        pen.moveTo(pts[0])
        for p in pts[1:]:
            pen.lineTo(p)
        pen.closePath()

    path([(0.41, 13.41), (6, 19), (7.41, 17.59), (1.82, 12)])
    path([(22.24, 5.59), (11.66, 16.17), (7.48, 12), (6.07, 13.41), (11.66, 19), (23.66, 7)])
    path([(18, 7), (16.59, 5.59), (10.25, 11.93), (11.66, 13.34)])
    return pen.glyph()


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
    alpha = np.pad(alpha, 1)
    ys, xs = np.nonzero(alpha > 0.5)
    if xs.size == 0:
        raise RuntimeError(f"No ink found in {path.name}")

    raw = measure.find_contours(alpha, 0.5)
    raw = [measure.approximate_polygon(c, SIMPLIFY_TOLERANCE) for c in raw]
    raw = [c for c in raw if len(c) >= 4]

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
            1
            for other_index, other in enumerate(polygons)
            if other_index != index and _contains(other, pts[0])
        )
        clockwise = _signed_area(pts) < 0
        if (depth % 2 == 0) != clockwise:
            pts = list(reversed(pts))
        pen.moveTo((round(pts[0][0]), round(pts[0][1])))
        for x, y in pts[1:]:
            pen.lineTo((round(x), round(y)))
        pen.closePath()

    return pen.glyph()


def main() -> int:
    if not FONT_PATH.is_file():
        print(f"Missing base font: {FONT_PATH}", file=sys.stderr)
        return 1

    src = TTFont(str(FONT_PATH))
    gs = src.getGlyphSet()
    hmtx_src = src["hmtx"].metrics

    glyph_order = [".notdef", "filter", "new-chat", "check", "double-check"]
    glyphs = {
        ".notdef": TTGlyphPen(None).glyph(),
        "filter": copy_glyph(gs, "filter"),
        "new-chat": copy_glyph(gs, "new-chat"),
        "check": glyph_check(),
        "double-check": glyph_double_check(),
    }
    cmap = {
        0xEA01: "filter",
        0xEA02: "new-chat",
        0xEA03: "check",
        0xEA04: "double-check",
    }
    advance = {
        ".notdef": (0, 0),
        "filter": hmtx_src.get("filter", (1666, 0)),
        "new-chat": hmtx_src.get("new-chat", (1000, 0)),
        "check": (1000, 0),
        "double-check": (1100, 0),
    }

    for name, code, filename, scale_factor in PNG_GLYPHS:
        png = EMOJI_ICONS / filename
        if not png.is_file():
            print(f"Missing icon: {png}", file=sys.stderr)
            return 1
        glyph = glyph_from_png(png, scale_factor)
        glyph.recalcBounds(None)
        glyphs[name] = glyph
        glyph_order.append(name)
        cmap[code] = name
        advance[name] = (UPM, glyph.xMin)
        print(f"  {code:04X} {name} <- {filename}")

    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics(advance)
    fb.setupHorizontalHeader(ascent=UPM, descent=0, lineGap=0)
    fb.setupOS2(
        version=4,
        sTypoAscender=UPM,
        sTypoDescender=0,
        sTypoLineGap=0,
        usWinAscent=UPM,
        usWinDescent=0,
        sCapHeight=UPM,
        sxHeight=UPM,
        fsSelection=0x00C0,
    )
    fb.setupPost()
    fb.setupNameTable(
        {
            "familyName": "UnisonIcons",
            "styleName": "Regular",
            "uniqueFontIdentifier": "UnisonIcons",
            "fullName": "UnisonIcons",
            "version": "Version 1.2",
            "psName": "UnisonIcons",
            "description": "Unison custom icons (filter, new-chat, checks, emoji categories). Regenerate via scripts/icons/generate-unison-icons.ps1",
        }
    )
    fb.setupHead(unitsPerEm=UPM)

    FONT_PATH.parent.mkdir(parents=True, exist_ok=True)
    fb.save(str(FONT_PATH))
    print(f"Wrote {FONT_PATH}")
    print(
        "  EA01 filter | EA02 new-chat | EA03 check | EA04 double-check | "
        "EA05 sports | EA06 nature | EA07 food | EA08 symbols"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
