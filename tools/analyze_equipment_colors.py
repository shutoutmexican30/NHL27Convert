"""Resolve NHL 24/NHL 27 home/away equipment fallbacks; read-only analysis."""
from collections import Counter
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPORT = ROOT / "Debugging" / "equipment-color-reference.json"
OUT = ROOT / "Debugging" / "equipment-color-analysis.json"
ZONES = [f"zone{i}" for i in range(1, 16)]
WORDS = "zero one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twentyone twentytwo twentythree twentyfour twentyfive".split()
MOD = json.loads((ROOT / "Debugging" / "installed-fanatics-mod.json").read_text(encoding="utf-8-sig"))
SOURCE_KITS = {(a["TeamId"], a["SourceKit"]): a for a in MOD["Additions"]}


def source_variant(addition):
    word = addition["SourceFolder27"].rstrip("/").split("/")[-1]
    return {"home": 0, "away": 1}.get(word, WORDS.index(word) if word in WORDS else None)


def resolve(rows, equipment, variant, brand=-1):
    rows = [r for r in rows if int(r["equipment"]) == equipment
            and int(r["era"]) in (-1, 0, 2) and int(r["variant"]) in (-1, variant)]
    branded = [r for r in rows if int(r["brand"]) == brand]
    candidates = branded or [r for r in rows if int(r["brand"]) == -1]
    if not candidates:
        return None
    preferred_era = 0 if variant in (0, 1) else 2
    def score(row):
        return (int(row["variant"]) == variant, int(row["era"]) == preferred_era, int(row["era"]) != -1)
    best_score = max(score(r) for r in candidates)
    best = [r for r in candidates if score(r) == best_score]
    if len(best) > 1:
        return dict(best[0], _candidates=best)
    return best[0]


def color(value):
    return f"#{int(value):06X}"


def main():
    teams = json.loads(REPORT.read_text(encoding="utf-8"))
    result = []
    missing = []
    for team in teams:
        palettes = Counter()
        for row in team["Goalie24"]:
            palettes.update(int(v) for v in row.values())
        palette_text = " ".join(f"{color(c)}({n})" for c, n in palettes.most_common()
                                if max((c >> 16) & 255, (c >> 8) & 255, c & 255)
                                - min((c >> 16) & 255, (c >> 8) & 255, c & 255) > 25)
        team_result = {"Team": team["Team"], "Name": team["Name"], "Kits": []}
        differences = 0
        additions = sorted((a for (team_id, _), a in SOURCE_KITS.items() if team_id == team["Team"]),
                           key=lambda a: a["SourceKit"] != "home")
        for addition in additions:
            variant = source_variant(addition)
            if variant is None:
                raise ValueError(f"Unknown source variant: {addition['SourceFolder27']}")
            kit = {"Variant": variant, "SourceKit": addition["SourceKit"],
                   "TargetVariant": addition["TargetVariant"], "SourceFolder27": addition["SourceFolder27"], "Equipment": []}
            for equipment in (4, 7):
                brands = sorted({int(r["brand"]) for r in team["Equipment24"] + team["Equipment27"]
                                 if int(r["equipment"]) == equipment})
                for brand in brands:
                    old = resolve(team["Equipment24"], equipment, variant, brand)
                    new = resolve(team["Equipment27"], equipment, variant, brand)
                    if old is None or new is None:
                        missing.append((team["Team"], variant, equipment, brand, old is None, new is None))
                        continue
                    changed = [z for z in ZONES if int(old[z]) != int(new[z])]
                    differences += len(changed)
                    kit["Equipment"].append({"Equipment": equipment, "Brand": brand, "Current24": old,
                                             "Source27": new, "Source27Candidates": new.get("_candidates", [new]),
                                             "Current24Candidates": old.get("_candidates", [old]), "DifferentZones": changed})
            team_result["Kits"].append(kit)
        result.append(team_result)
        summary = []
        for kit in team_result["Kits"]:
            generic = [e for e in kit["Equipment"] if e["Brand"] == -1]
            summary.extend(f"type{e['Equipment']}v{kit['Variant']}=" + "/".join(color(e['Source27'][z]) for z in ("zone1", "zone2", "zone3", "zone7"))
                           for e in generic)
        brand_count = len({e['Brand'] for k in team_result['Kits'] for e in k['Equipment']})
        print(f"{team['Team']:2} {team['Name'][:25]:25} diff={differences:2} brands={brand_count}  {' '.join(summary)}")
        print(f"   NHL24 goalie chromatic colors: {palette_text or '(none)'}")
    OUT.write_text(json.dumps({"Teams": result, "Missing": missing}, indent=2) + "\n", encoding="utf-8")
    print(f"Resolved equipment report: {OUT}")
    print(f"Unresolved team/variant/equipment/brand rows: {len(missing)}")
    if missing:
        print(missing)


if __name__ == "__main__":
    main()
