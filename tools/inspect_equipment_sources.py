"""Summarize current-kit equipment donors without changing game data."""
import csv
import json
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "reference" / "nhl27-full-csv"


def rows(name):
    with (SOURCE / f"{name}.csv").open(encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))


def color(value):
    return f"#{int(value):06X}"


def valid(name, data):
    metadata = json.loads((SOURCE / "tables.json").read_text(encoding="utf-8-sig"))
    count = next(int(x["valid"]) for x in metadata if x.get("name", x.get("table")) == name)
    return data[:count]


def main():
    equipment = valid("stockteamequipmenttable", rows("stockteamequipmenttable"))
    jerseys = valid("stockteamjerseys", rows("stockteamjerseys"))
    goalies = valid("stockgoalieequipment", rows("stockgoalieequipment"))
    mod = json.loads((ROOT / "Debugging" / "installed-fanatics-mod.json").read_text(encoding="utf-8-sig"))
    variants = {"home": 0, "away": 1, **{word: i for i, word in enumerate(
        "zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twentyone twentytwo twentythree twentyfour twentyfive".split())}}
    print("GOALIE ROW IDENTITY / PRINCIPAL PAD ZONES")
    for team in range(32):
        group = [r for r in goalies if int(r["team"]) == team and int(r["league"]) == 0]
        for r in group:
            identity = {k: v for k, v in r.items() if "zone" not in k}
            pads = Counter(int(v) for k, v in r.items() if k.startswith("padszone"))
            print(team, identity, "pads:", " ".join(f"{color(c)}={n}" for c, n in pads.most_common(7)))
    print("\nSOURCE EQUIPMENT DONOR RANKING")
    for a in mod["Additions"]:
        team = a["TeamId"]
        variant = variants[a["SourceFolder27"].rstrip("/").split("/")[-1]]
        source_jersey = [r for r in jerseys if int(r["teamid"]) == team and int(r["style"]) == 2 and int(r["variant"]) == variant]
        candidates = [r for r in equipment if int(r["team"]) == team and int(r["equipment"]) in (4, 7)
                      and int(r["variant"]) in (-1, variant) and int(r["era"]) in (-1, 0, 2)]
        exact = [r for r in candidates if int(r["variant"]) == variant and int(r["era"]) == 2]
        wildcard = [r for r in candidates if int(r["variant"]) == -1 and int(r["era"]) == 2]
        chosen = exact or wildcard
        branded = Counter(int(r["brand"]) for r in chosen)
        dupes = [(k, n) for k, n in Counter((r["equipment"], r["brand"]) for r in chosen).items() if n > 1]
        print(f"{team:2} {a['Team']:15} {a['SourceKit']:4} source={variant:2} -> {a['TargetVariant']:2} "
              f"jerseyBrand={[r.get('brand', '<not a jersey field>') for r in source_jersey]} selectedBrands={dict(branded)} duplicateTypeBrand={dupes}")
        if variant == 10 and team == 27 or dupes:
            for r in candidates:
                print("   ", {k: r[k] for k in ("index", "equipment", "era", "variant", "brand", "jerseyartid")},
                      "zones", " ".join(color(r[f"zone{i}"]) for i in range(1, 5)))
        for equipment_id in (4, 7):
            generic = [r for r in chosen if int(r["equipment"]) == equipment_id and int(r["brand"]) == -1]
            if generic:
                continue
            alternatives = [r for r in equipment if int(r["team"]) == team and int(r["equipment"]) == equipment_id]
            print(f"    NO ERA-2 GENERIC TYPE {equipment_id}: " + "; ".join(
                f"row={r['index']} era={r['era']} variant={r['variant']} brand={r['brand']} art={r['jerseyartid']} "
                + "/".join(color(r[f'zone{i}']) for i in range(1, 5)) for r in alternatives))
    print("\nEQUIPMENT TABLE SPARE CAPACITY")
    meta = json.loads((SOURCE / "tables.json").read_text(encoding="utf-8-sig"))
    print(next(x for x in meta if x.get("name", x.get("table")) == "stockteamequipmenttable"))


if __name__ == "__main__":
    main()
