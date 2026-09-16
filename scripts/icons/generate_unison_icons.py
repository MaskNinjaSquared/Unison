#!/usr/bin/env python3
"""Rebuild UnisonIcons.ttf: preserve filter/new-chat, add message checkmarks.

Codepoints (Private Use Area):
  EA01  filter
  EA02  new-chat
  EA03  check (single tick — sent / pending)
  EA04  double-check (delivered / read; color via Foreground)

Requires: pip install fonttools
"""

from __future__ import annotations

import sys
from pathlib import Path

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont

REPO = Path(__file__).resolve().parents[2]
FONT_PATH = REPO / "src" / "Unison.Uwp" / "Assets" / "Fonts" / "UnisonIcons.ttf"
UPM = 1000


def copy_glyph(gs, name):
    pen = TTGlyphPen(None)
    gs[name].draw(pen)
    return pen.glyph()


def _svg_to_font(x: float, y: float, size: float = 24.0):
    """Map Material-style SVG coords (y-down, size×size) into font space (y-up, UPM)."""
    s = UPM / size
    return (x * s, (size - y) * s)


def glyph_check():
    """Single filled check (Material 'done'), centered in the em square."""
    pen = TTGlyphPen(None)
    # M9 16.2 L4.8 12 L3.4 13.4 L9 19 L21 7 L19.6 5.6 Z
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
    """Double filled check (Material 'done_all')."""
    pen = TTGlyphPen(None)

    def path(coords):
        pts = [_svg_to_font(x, y) for x, y in coords]
        pen.moveTo(pts[0])
        for p in pts[1:]:
            pen.lineTo(p)
        pen.closePath()

    # Left small tick
    path([(0.41, 13.41), (6, 19), (7.41, 17.59), (1.82, 12)])
    # Right / main tick
    path([(22.24, 5.59), (11.66, 16.17), (7.48, 12), (6.07, 13.41), (11.66, 19), (23.66, 7)])
    # Bridge piece
    path([(18, 7), (16.59, 5.59), (10.25, 11.93), (11.66, 13.34)])
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

    advance = {
        ".notdef": (0, 0),
        "filter": hmtx_src.get("filter", (1666, 0)),
        "new-chat": hmtx_src.get("new-chat", (1000, 0)),
        "check": (1000, 0),
        "double-check": (1100, 0),
    }

    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap(
        {
            0xEA01: "filter",
            0xEA02: "new-chat",
            0xEA03: "check",
            0xEA04: "double-check",
        }
    )
    fb.setupGlyf(glyphs)
    fb.setupHorizontalMetrics(advance)
    fb.setupHorizontalHeader(ascent=UPM, descent=0)
    fb.setupOS2(
        sTypoAscender=UPM,
        sTypoDescender=0,
        usWinAscent=1090,
        usWinDescent=0,
    )
    fb.setupPost()
    fb.setupNameTable(
        {
            "familyName": "UnisonIcons",
            "styleName": "Regular",
            "uniqueFontIdentifier": "UnisonIcons",
            "fullName": "UnisonIcons",
            "version": "Version 1.1",
            "psName": "UnisonIcons",
            "description": "Unison custom icons (filter, new-chat, message checks). Regenerate via scripts/icons/generate-unison-icons.ps1",
        }
    )
    fb.setupHead(unitsPerEm=UPM)

    FONT_PATH.parent.mkdir(parents=True, exist_ok=True)
    fb.save(str(FONT_PATH))
    print(f"Wrote {FONT_PATH}")
    print("  EA01 filter | EA02 new-chat | EA03 check | EA04 double-check")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
