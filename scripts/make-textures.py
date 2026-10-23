#!/usr/bin/env python3
"""Draws the mod's textures from simple shapes so every image is original and reproducible.

Usage: python scripts/make-textures.py   (writes into Mod/Textures/Things/Building/)
Requires Pillow. The output is committed; rerun only when changing the art.
"""
from pathlib import Path

from PIL import Image, ImageDraw

OUT = Path(__file__).resolve().parent.parent / "Mod" / "Textures" / "Things" / "Building"
PX = 128  # pixels per cell

WOOD = (138, 96, 58, 255)
WOOD_DARK = (96, 64, 38, 255)
STEEL = (128, 134, 140, 255)
STEEL_DARK = (74, 80, 88, 255)
STEEL_LIGHT = (176, 182, 188, 255)
SEAL = (178, 38, 38, 255)
GOLD = (214, 176, 62, 255)
OUTLINE = (30, 30, 34, 255)
CLEAR = (0, 0, 0, 0)


def canvas(w_cells, h_cells):
    img = Image.new("RGBA", (int(PX * w_cells), int(PX * h_cells)), CLEAR)
    return img, ImageDraw.Draw(img)


def crate(d, x0, y0, size):
    """Sealed crate: planks, two steel bands, a red seal."""
    x1, y1 = x0 + size, y0 + size
    d.rectangle([x0, y0, x1, y1], fill=WOOD, outline=OUTLINE, width=max(2, size // 24))
    for i in range(1, 4):
        y = y0 + size * i // 4
        d.line([x0 + 3, y, x1 - 3, y], fill=WOOD_DARK, width=max(1, size // 40))
    band = max(3, size // 10)
    for fx in (0.22, 0.78):
        x = x0 + int(size * fx)
        d.rectangle([x - band // 2, y0, x + band // 2, y1], fill=STEEL_DARK)
    r = size // 7
    cx, cy = x0 + size // 2, y0 + size // 2
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=SEAL, outline=OUTLINE, width=2)


def cargo():
    img, d = canvas(1, 1)
    crate(d, 14, 14, PX - 28)
    img.save(OUT / "Cargo.png")


def reception():
    img, d = canvas(1, 1)
    d.rectangle([10, 30, PX - 10, PX - 14], fill=WOOD, outline=OUTLINE, width=4)
    d.rectangle([10, 30, PX - 10, 48], fill=WOOD_DARK, outline=OUTLINE, width=3)
    d.rectangle([34, 58, 94, 98], fill=(232, 226, 208, 255), outline=OUTLINE, width=3)  # ledger
    for y in range(66, 94, 8):
        d.line([42, y, 86, y], fill=(120, 120, 120, 255), width=2)
    d.ellipse([96, 14, 116, 34], fill=GOLD, outline=OUTLINE, width=2)  # bell
    img.save(OUT / "Reception.png")


def rack():
    north, d = canvas(1, 2)
    w, h = north.size
    d.rectangle([6, 6, w - 6, h - 6], outline=OUTLINE, width=6)
    d.rectangle([12, 12, w - 12, h - 12], outline=STEEL, width=8)
    for y in (h // 2,):
        d.rectangle([12, y - 5, w - 12, y + 5], fill=STEEL_DARK)
    for x, y in ((6, 6), (w - 18, 6), (6, h - 18), (w - 18, h - 18)):
        d.rectangle([x, y, x + 12, y + 12], fill=STEEL_LIGHT, outline=OUTLINE, width=2)
    north.save(OUT / "Rack_north.png")
    north.save(OUT / "Rack_south.png")
    north.rotate(90, expand=True).save(OUT / "Rack_east.png")


def vault():
    img, d = canvas(2, 2)
    w, h = img.size
    d.rounded_rectangle([8, 8, w - 8, h - 8], radius=18, fill=STEEL, outline=OUTLINE, width=8)
    d.rounded_rectangle([30, 30, w - 30, h - 30], radius=12, fill=STEEL_DARK, outline=OUTLINE, width=4)
    cx, cy = w // 2, h // 2
    d.ellipse([cx - 56, cy - 56, cx + 56, cy + 56], fill=STEEL_LIGHT, outline=OUTLINE, width=6)
    d.ellipse([cx - 14, cy - 14, cx + 14, cy + 14], fill=STEEL_DARK, outline=OUTLINE, width=3)
    for dx, dy in ((0, -1), (1, 0), (0, 1), (-1, 0)):
        d.line([cx + dx * 16, cy + dy * 16, cx + dx * 50, cy + dy * 50], fill=OUTLINE, width=8)
    for y in (60, h - 60):
        d.rectangle([w - 44, y - 16, w - 22, y + 16], fill=STEEL_LIGHT, outline=OUTLINE, width=3)  # hinges
    img.save(OUT / "Vault.png")


def return_pod():
    img, d = canvas(1, 2)
    w, h = img.size
    d.rounded_rectangle([12, 16, w - 12, h - 16], radius=40, fill=STEEL_LIGHT, outline=OUTLINE, width=6)
    d.rounded_rectangle([28, 44, w - 28, h - 44], radius=24, fill=STEEL, outline=OUTLINE, width=4)
    d.rectangle([24, h // 2 - 8, w - 24, h // 2 + 8], fill=(230, 160, 40, 255), outline=OUTLINE, width=2)  # hazard band
    for y in (70, h - 70):
        d.ellipse([w // 2 - 12, y - 12, w // 2 + 12, y + 12], fill=STEEL_DARK, outline=OUTLINE, width=2)
    img.save(OUT / "ReturnPod.png")


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    cargo()
    reception()
    rack()
    vault()
    return_pod()
    for p in sorted(OUT.glob("*.png")):
        print(p.relative_to(OUT.parent.parent.parent), Image.open(p).size)


if __name__ == "__main__":
    main()
