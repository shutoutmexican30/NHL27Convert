"""Read-only Fanatics source audit. Copies external source art before inspecting it.

Run from the workspace: python tools/audit_fanatics_sources.py
The script writes only Debugging/fanatics-source-review; it never writes a game.
"""
from __future__ import annotations

from collections import Counter
import csv
import json
from pathlib import Path
import shutil

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "Debugging" / "fanatics-source-review"
ART = OUT / "art"
MOD = json.loads((ROOT / "Debugging" / "installed-fanatics-mod.json").read_text(encoding="utf-8-sig"))
REPORT = json.loads((ROOT / "Debugging" / "equipment-color-reference.json").read_text(encoding="utf-8"))
WORDS = "zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twentyone twentytwo twentythree twentyfour twentyfive".split()
ZONES = [f"zone{i}" for i in range(1, 16)]


def load_csv(name):
    with (ROOT / "reference" / "nhl27-full-csv" / f"{name}.csv").open(encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))


def integer(row, key):
    return int(row[key])


def color(value):
    return f"#{int(value):06X}"


def source_variant(addition):
    word = addition["SourceFolder27"].rstrip("/").split("/")[-1]
    return WORDS.index(word)


def select_font(size):
    for name in ("arial.ttf", "segoeui.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def thumbnail(path, width=155, height=125):
    image = Image.open(path).convert("RGBA")
    alpha = image.getchannel("A")
    if alpha.getextrema()[0] < 255:
        background = Image.new("RGBA", image.size, "#E7E7E7")
        background.alpha_composite(image)
        image = background
    else:
        image = Image.alpha_composite(Image.new("RGBA", image.size, "#E7E7E7"), image)
    image.thumbnail((width - 8, height - 8), Image.Resampling.LANCZOS)
    tile = Image.new("RGB", (width, height), "#E7E7E7")
    tile.paste(image.convert("RGB"), ((width - image.width) // 2, (height - image.height) // 2))
    return tile


def copy_art(additions):
    """Stage only referenced logo art. Inspect the staged copy, never the external cache."""
    copied = {}
    for addition in additions:
        for texture in addition["Textures"]:
            if "/branding_jersey_" not in texture["SourceSlot"]:
                continue
            roles = texture.get("RequiredRoles", [])
            for layer, external in (texture["Artwork"].get("Layers") or {}).items():
                key = (addition["Team"], addition["SourceKit"], texture["SourceSlot"], int(layer))
                local = ART / addition["Team"] / addition["SourceKit"] / (Path(external).name)
                if not local.exists():
                    local.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(external, local)
                copied[key] = {"path": local, "role": roles[int(layer)] if int(layer) < len(roles) else "?"}
    return copied


def equipment_rows(rows, team):
    result = []
    for order, row in enumerate(rows):
        if integer(row, "team") != team or integer(row, "equipment") not in (4, 7):
            continue
        if integer(row, "era") not in (-1, 0, 2) or not -1 <= integer(row, "variant") <= 25:
            continue
        result.append({**{k: integer(row, k) for k in ("team", "brand", "jerseyartid", "equipment", "era", "variant", "index")},
                       "order": order, "colors": [integer(row, z) for z in ZONES]})
    return result


def resolve_candidates(rows, equipment, variant, brand):
    """Show exact and wildcard choices; deliberately does not select a writer row yet."""
    candidates = [r for r in rows if r["equipment"] == equipment and r["brand"] == brand
                  and r["variant"] in (-1, variant)]
    exact = [r for r in candidates if r["variant"] == variant]
    return {"requestedVariant": variant, "exact": exact, "wildcard": [r for r in candidates if r["variant"] == -1]}


def main():
    OUT.mkdir(exist_ok=True)
    additions = MOD["Additions"]
    staged = copy_art(additions)
    equipment = load_csv("stockteamequipmenttable")
    jerseys = load_csv("stockteamjerseys")
    evidence = []
    for team in REPORT:
        team_id = team["Team"]
        rows = equipment_rows(equipment, team_id)
        kits = []
        for addition in (a for a in additions if a["TeamId"] == team_id):
            variant = source_variant(addition)
            source_jerseys = [{k: r[k] for k in ("style", "variant", "isdefault", "islightcolor", "teamartid", "helmetcolorpreset", "glovescolorpreset", "helmetvariant", "glovesvariant", "helmetlogo", "helmetlogotype")}
                              for r in jerseys if integer(r, "teamid") == team_id and integer(r, "variant") == variant and integer(r, "style") == 2]
            brands = sorted({r["brand"] for r in rows})
            kits.append({"sourceKit": addition["SourceKit"], "sourceVariant": variant,
                         "sourceFolder27": addition["SourceFolder27"], "sourceJerseys": source_jerseys,
                         "equipment": {str(eq): {str(brand): resolve_candidates(rows, eq, variant, brand) for brand in brands}
                                       for eq in (4, 7)}})
        evidence.append({"team": team_id, "name": team["Name"], "goalie24": team["Goalie24"], "kits": kits,
                         "equipmentRows": rows if team_id in (0, 22, 24, 25, 27) else []})
    (OUT / "equipment-evidence.json").write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")

    for team_id in (0, 22, 24, 25, 27):
        team = evidence[team_id]
        print(f"\n{team['name']} (team {team_id})")
        for kit in team["kits"]:
            print(f"  {kit['sourceKit']} variant {kit['sourceVariant']}: {kit['sourceJerseys']}")
            for eq, brands in kit["equipment"].items():
                for brand, choices in brands.items():
                    exact = ["e{} b{} v{}: {}".format(r["era"], r["brand"], r["variant"], " ".join(color(c) for c in r["colors"][:4])) for r in choices["exact"]]
                    wild = ["e{} b{} v{}: {}".format(r["era"], r["brand"], r["variant"], " ".join(color(c) for c in r["colors"][:4])) for r in choices["wildcard"]]
                    print(f"    type {eq} brand {brand}: exact [{'; '.join(exact)}]; wildcard [{'; '.join(wild)}]")

    font = select_font(16)
    small = select_font(13)
    for addition in additions:
        color_texture = next(t for t in addition["Textures"] if "/branding_jersey_" in t["SourceSlot"] and t["SourceSlot"].endswith("_color_array"))
        roles = color_texture["RequiredRoles"]
        width, row_height = 960, 168
        sheet = Image.new("RGB", (width, row_height + 42), "#14202C")
        draw = ImageDraw.Draw(sheet)
        draw.text((12, 10), f"{addition['Team']}  |  {addition['SourceKit']}  |  NHL 27 variant {source_variant(addition)}", font=font, fill="white")
        for layer in sorted(int(n) for n in color_texture["Artwork"]["Layers"]):
            key = (addition["Team"], addition["SourceKit"], color_texture["SourceSlot"], layer)
            item = staged[key]
            x = layer * 158
            sheet.paste(thumbnail(item["path"]), (x, 40))
            draw.text((x + 4, row_height + 6), f"{layer}: {roles[layer]}", font=small, fill="white")
        path = OUT / f"{addition['Team']}-{addition['SourceKit']}.png"
        sheet.save(path)
    print(f"\nCopied and reviewed source metadata for {len(additions)} kits. Review sheets: {OUT}")
    print("No source selection is an approved install plan; inspect exact/wildcard conflicts before writing.")


if __name__ == "__main__":
    main()
