#!/usr/bin/env python3
"""Build WhatsAppLegacyIcons.ttf from Assets/WhatsAppLegacy/Icons PNGs.

Codepoints (Private Use Area):
  EB01  search   (appbar-search.png)
  EB02  add      (appbar-add.png)
  EB03  attach   (composer-attach.png)
  EB04  send     (composer-send.png)
  EB05  mic      (composer-mic.png)

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
ICONS = REPO / "src" / "Unison.Uwp" / "Assets" / "WhatsAppLegacy" / "Icons"
FONT_PATH = REPO / "src" / "Unison.Uwp" / "Assets" / "Fonts" / "WhatsAppLegacyIcons.ttf"
UPM = 1000
PAD = 0.10

# name → (codepoint, png filename)
GLYPHS = [
    ("search", 0xEB01, "appbar-search.png"),
    ("add", 0xEB02, "appbar-add.png"),
    ("attach", 0xEB03, "composer-attach.png"),
    ("send", 0xEB04, "composer-send.png"),
    ("mic", 0xEB05, "composer-mic.png"),
]


def _white_mask(path: Path) -> np.ndarray:
    im = Image.open(path).convert("RGBA")
    arr = np.asarray(im, dtype=np.float32)
    rgb = arr[:, :, :3].mean(axis=2)
    alpha = arr[:, :, 3]
    # White ink on black (or transparent) — keep bright opaque pixels.
    return (rgb > 140.0) & (alpha > 40.0)


def _contour_area(contour: np.ndarray) -> float:
    # Shoelace; contour is (row, col) = (y, x).
    y = contour[:, 0]
    x = contour[:, 1]
    return 0.5 * abs(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1)))


def glyph_from_png(path: Path):
    mask = _white_mask(path)
    h, w = mask.shape
    # find_contours wants float image; level 0.5 on boolean works.
    contours = measure.find_contours(mask.astype(float), 0.5)
    if not contours:
        raise RuntimeError(f"No ink found in {path.name}")

    # Drop dust; keep largest shapes (outer + holes of the icon).
    contours = sorted(contours, key=_contour_area, reverse=True)
    min_area = max(8.0, _contour_area(contours[0]) * 0.01)
    contours = [c for c in contours if _contour_area(c) >= min_area]
    if not contours:
        raise RuntimeError(f"Contours too small in {path.name}")

    scale = (UPM * (1.0 - 2.0 * PAD)) / float(max(w, h))
    ox = (UPM - w * scale) / 2.0
    oy = (UPM - h * scale) / 2.0

    pen = TTGlyphPen(None)
    for contour in contours:
        # contour rows = y (down), cols = x
        pts = []
        for y, x in contour:
            fx = ox + x * scale
            fy = UPM - (oy + y * scale)
            pts.append((fx, fy))
        if len(pts) < 3:
            continue
        pen.moveTo(pts[0])
        for p in pts[1:]:
            pen.lineTo(p)
        pen.closePath()

    return pen.glyph()


def main() -> int:
    glyph_order = [".notdef"]
    glyphs = {".notdef": TTGlyphPen(None).glyph()}
    cmap = {}
    advance = {".notdef": (0, 0)}

    for name, code, filename in GLYPHS:
        png = ICONS / filename
        if not png.is_file():
            print(f"Missing icon: {png}", file=sys.stderr)
            return 1
        glyphs[name] = glyph_from_png(png)
        glyph_order.append(name)
        cmap[code] = name
        advance[name] = (UPM, 0)
        print(f"  {code:04X} {name} <- {filename}")

    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics(advance)
    fb.setupHorizontalHeader(ascent=UPM, descent=0)
    fb.setupOS2(
        sTypoAscender=UPM,
        sTypoDescender=0,
        usWinAscent=UPM,
        usWinDescent=0,
    )
    fb.setupPost()
    fb.setupNameTable(
        {
            "familyName": "WhatsAppLegacyIcons",
            "styleName": "Regular",
            "uniqueFontIdentifier": "WhatsAppLegacyIcons",
            "fullName": "WhatsAppLegacyIcons",
            "version": "Version 1.0",
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
