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


def main():
    n24_csv, n27_csv, f24, f27, out_path = sys.argv[1:6]
    folders24 = set(open(f24, encoding="utf-8").read().split())
    folders27 = set(open(f27, encoding="utf-8").read().split())
    a, b = side(n24_csv, folders24), side(n27_csv, folders27)
    teams = []
    for idx in sorted(set(a) | set(b), key=int):
        if idx not in a or idx not in b:
            continue
        teams.append({"index": int(idx), "name24": a[idx]["name"], "name27": b[idx]["name"],
                      "nhl24": {k: v for k, v in a[idx].items() if k != "name"},
                      "nhl27": {k: v for k, v in b[idx].items() if k != "name"}})
    doc = {
        "format": 1,
        "comment": "Hand-editable. Default home (dark) / away (light) jersey variant per team, from stockteamjerseys "
                   "(isdefault=1, islightcolor). Folder = texture set folder (0 home, 1 away, n = number word). "
                   "The Convert 27 matcher pairs NHL 24 home with NHL 27 home and away with away using this file.",
        "teams": teams,
    }
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=1)
    print(f"{len(teams)} teams")


if __name__ == "__main__":
    main()
