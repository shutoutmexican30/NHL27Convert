"""Build a local 2026-27 NHL uniform reference contact sheet.

The drawings remain personal reference material from nhluniforms.com; they are not mod textures.
Run from the repository root: python tools/build_uniform_reference_sheet.py [season]
"""
from __future__ import annotations

import html
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
SEASON = sys.argv[1] if len(sys.argv) > 1 else "2026-27"
SITE = "https://www.nhluniforms.com"
OUT = ROOT / "Debugging" / "uniform-reference" / SEASON
IMAGES = OUT / "teams"
PAGE_URL = f"{SITE}/Mobile/{SEASON}.html"


def fetch(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "CenterIceModdingSuite/1.0 (personal reference check)"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return response.read()


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    IMAGES.mkdir(parents=True, exist_ok=True)
    index = OUT / "mobile.html"
    if not index.exists():
        index.write_bytes(fetch(PAGE_URL))
    page = index.read_text(encoding="utf-8")
    pattern = re.compile(
        r'<a href="/Mobile/([^/]+)/[^"]+"><img src="(/Mobile/\1/\1\d+\.png)"[^>]*></a>'
        r'<br><h2><div align="right">([^<]+)</div>',
        re.IGNORECASE,
    )
    entries = []
    seen = set()
    for match in pattern.finditer(page):
        key, relative, name = match.groups()
        if key in seen:
            continue
        seen.add(key)
        url = SITE + relative
        png = IMAGES / f"{key}.png"
        if not png.exists():
            print(f"Downloading {html.unescape(name)}", flush=True)
            png.write_bytes(fetch(url))
            time.sleep(0.25)
        with Image.open(png) as image:
            entries.append({"team": html.unescape(name), "key": key, "url": url,
                            "file": str(png.relative_to(ROOT)), "width": image.width, "height": image.height,
                            "kits": max(1, image.height // 450)})
    if len(entries) != 32:
        raise RuntimeError(f"Expected 32 NHL teams, found {len(entries)}; inspect {index}")

    columns, cell_w, cell_h, margin = 4, 300, 430, 20
    rows = (len(entries) + columns - 1) // columns
    sheet = Image.new("RGB", (margin + columns * cell_w, 80 + margin + rows * cell_h), "#111820")
    draw = ImageDraw.Draw(sheet)
    try:
        title_font = ImageFont.truetype("arial.ttf", 28)
        label_font = ImageFont.truetype("arial.ttf", 18)
        small_font = ImageFont.truetype("arial.ttf", 13)
    except OSError:
        title_font = label_font = small_font = ImageFont.load_default()
    draw.text((margin, 18), f"NHL uniforms {SEASON} — reference contact sheet", fill="white", font=title_font)
    draw.text((margin, 52), "Source: nhluniforms.com — personal visual reference, not game textures", fill="#aab7c4", font=small_font)
    for i, entry in enumerate(entries):
        x = margin + (i % columns) * cell_w
        y = 80 + margin + (i // columns) * cell_h
        with Image.open(ROOT / entry["file"]) as source:
            image = source.convert("RGB")
            image.thumbnail((cell_w - 20, cell_h - 62), Image.Resampling.LANCZOS)
            sheet.paste(image, (x + (cell_w - image.width) // 2, y + 42))
        draw.text((x + 8, y + 7), entry["team"], fill="white", font=label_font)
        draw.text((x + 8, y + 27), f"{entry['kits']} kit{'s' if entry['kits'] != 1 else ''}", fill="#8fa4b5", font=small_font)
    output = OUT / "contact-sheet.jpg"
    sheet.save(output, quality=91, optimize=True)
    (OUT / "manifest.json").write_text(json.dumps({"season": SEASON, "page": PAGE_URL, "teams": entries}, indent=2), encoding="utf-8")
    print(f"{len(entries)} teams; {output.relative_to(ROOT)}; {output.stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
