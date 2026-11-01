"""Builds data/variant_map.json: each team's default home (dark) and away (light) jersey variant in NHL 24 and
NHL 27, from stockteamjerseys (isdefault = 1, islightcolor) and exhibitionteams, and the texture folder each
variant lives in (0 = home, 1 = away, n = the number as a word, e.g. 13 = thirteen).

usage: python make_variant_map.py <nhl24 csv dir> <nhl27 csv dir> <nhl24 texture folders.txt> <nhl27 texture folders.txt> <out.json>
The folder lists are "team/brand/set" lines (used to check that each mapped folder exists).
"""
import csv
import json
import sys

NUMBERS = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
           "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty", "twentyone",
           "twentytwo", "twentythree", "twentyfour", "twentyfive"]
CITY_FOLDER = {"NY Islanders": "nyi", "NY Rangers": "nyr"}


def folder_of_variant(v):
    v = int(v)
    return "home" if v == 0 else "away" if v == 1 else NUMBERS[v]


def team_folder(city):
    return CITY_FOLDER.get(city, "".join(c for c in city.lower() if c.isalnum()))


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        return list(csv.DictReader(f))


def side(csv_dir, folders):
    teams = {r["index"]: r for r in load(f"{csv_dir}/exhibitionteams.csv")}
    out = {}
    for r in load(f"{csv_dir}/stockteamjerseys.csv"):
        if r["isdefault"] != "1":
            continue
        t = teams.get(r["teamid"])
        if not t or not t.get("cityname"):
            continue
        d = out.setdefault(r["teamid"], {"name": t["teamname"], "folder": team_folder(t["cityname"])})
        key = "away" if r["islightcolor"] == "1" else "home"
        d[key + "Variant"] = int(r["variant"])
        d[key] = folder_of_variant(r["variant"])
    for d in out.values():
        for k in ("home", "away"):
            if k in d:
                d[k + "Exists"] = any(f"{d['folder']}/{b}/{d[k]}" in folders for b in ("adidas", "rbk", "nineties"))
    return out


def extra_kits(team, folders24, folders27):
    """NHL 27 adidas sets the team has and NHL 24 has no folder for, with a suggested NHL 24 set to replace."""
    t24, t27 = team["nhl24"], team["nhl27"]
    have24 = sorted(f.split("/")[2] for f in folders24 if f.startswith(t24["folder"] + "/adidas/"))
    have27 = sorted(f.split("/")[2] for f in folders27 if f.startswith(t27["folder"] + "/adidas/"))
    defaults27 = {t27.get("home"), t27.get("away")}
    extras = [s for s in have27 if s not in have24 and s not in defaults27]
    order = {w: i for i, w in enumerate(NUMBERS)}
    # Old alternates first: never NHL 24's own home/away, and sets NHL 27 still ships under the same name come last.
    free = [s for s in have24 if s not in ("home", "away", t24.get("home"), t24.get("away"))]
    free.sort(key=lambda s: (s in have27, order.get(s, 99)))
    out = []
    for s in sorted(extras, key=lambda s: order.get(s, 99)):
        out.append({"nhl27": s, "variant27": order.get(s), "replaces24": free.pop(0) if free else None})
    return out


def main():
    n24_csv, n27_csv, f24, f27, out_path = sys.argv[1:6]
    folders24 = set(open(f24, encoding="utf-8").read().split())
    folders27 = set(open(f27, encoding="utf-8").read().split())
    a, b = side(n24_csv, folders24), side(n27_csv, folders27)
    teams = []
    for idx in sorted(set(a) | set(b), key=int):
        if idx not in a or idx not in b:
            continue
        team = {"index": int(idx), "name24": a[idx]["name"], "name27": b[idx]["name"],
                "nhl24": {k: v for k, v in a[idx].items() if k != "name"},
                "nhl27": {k: v for k, v in b[idx].items() if k != "name"}}
        team["extraKits"] = extra_kits(team, folders24, folders27)
        teams.append(team)
    doc = {
        "format": 2,
        "comment": "Hand-editable. Default home (dark) / away (light) jersey variant per team, from stockteamjerseys "
                   "(isdefault=1, islightcolor). Folder = texture set folder (0 home, 1 away, n = number word). "
                   "The Convert 27 matcher pairs NHL 24 home with NHL 27 home and away with away using this file. "
                   "extraKits: NHL 27 adidas sets with no NHL 24 folder (besides the default home/away). NHL 24 cannot get new "
                   "texture folders, so each can replace an existing NHL 24 set: 'replaces24' is a suggestion (an old adidas "
                   "alternate, never home/away/retro); set it to null to skip. Nothing is applied from here.",
        "teams": teams,
    }
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1)
    print(f"{len(teams)} teams")


if __name__ == "__main__":
    main()
