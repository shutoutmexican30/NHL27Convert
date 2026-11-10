"""Summarize the read-only installed-branding report without changing game files."""
from __future__ import annotations

import json
import re
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPORT = ROOT / "Debugging" / "fanatics-branding-audit" / "branding-report.json"
results = json.loads(REPORT.read_text(encoding="utf-8-sig"))
by_team: dict[str, list[str]] = defaultdict(list)
sponsors: dict[str, dict[str, int]] = defaultdict(dict)
categories = Counter()
roles = Counter()
for result in results:
    team = result["Layers"][0]["Team"] if result["Layers"] else next(iter(result["Problems"]), "unknown").split(" ", 1)[0]
    for layer in result["Layers"]:
        sponsors[team][layer["Kit"]] = result["IntendedSponsors"]
        break
    for problem in result["Problems"]:
        by_team[team].append(problem)
        if "coeff difference" in problem:
            categories["coefficient"] += 1
        elif "normal difference" in problem:
            categories["normal"] += 1
        elif "color difference" in problem:
            categories["color"] += 1
        elif "unconverted visible stock" in problem:
            categories["retained stock"] += 1
        elif "copies match" in problem:
            categories["layout/audit exception"] += 1
        elif "count is" in problem:
            categories["role count"] += 1
            match = re.search(r": (\w+) count is", problem)
            if match:
                roles[match.group(1)] += 1
        else:
            categories["other"] += 1
print(f"Uniforms: {len(results)}; array copies: {sum(r['ArrayCopies'] for r in results)}")
print(f"Intended sponsors: {sum(r['IntendedSponsors'] for r in results)}")
print("Problem categories:", dict(categories))
print("Role-count problems:", dict(roles))
print("\nPer-uniform intended sponsors:")
for team in sorted(sponsors):
    print(f"  {team:16} home={sponsors[team].get('home', '?')} away={sponsors[team].get('away', '?')}")
print("\nProblems by uniform:")
for result in results:
    if result["Problems"]:
        team = result["Layers"][0]["Team"] if result["Layers"] else result["Problems"][0].split(" ", 1)[0]
        kit = result["Layers"][0]["Kit"] if result["Layers"] else "?"
        counts = Counter("coefficient" if "coeff difference" in p else "normal" if "normal difference" in p
                         else "color" if "color difference" in p else "stock" if "unconverted visible stock" in p
                         else "layout" if "copies match" in p else "role" if "count is" in p else "other"
                         for p in result["Problems"])
        print(f"  {team:16} {kit:5} {len(result['Problems']):3}: {dict(counts)}")
print("\nNon-coefficient problems:")
for result in results:
    for problem in result["Problems"]:
        if "coeff difference" not in problem:
            print("  " + problem)
