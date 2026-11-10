"""Package only the current NHL teams' helmet/glove source rows used by new-uniform planning."""
from __future__ import annotations

import csv
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "reference" / "nhl27-full-csv"
TARGET = ROOT / "data" / "nhl27-equipment"
TABLE = "stockteamequipmenttable"


def main() -> None:
    with (SOURCE / f"{TABLE}.csv").open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        header = reader.fieldnames
        assert header
        rows = [row for row in reader if row["team"].lstrip("-").isdigit()
                and 0 <= int(row["team"]) <= 31 and row["equipment"] in ("4", "7")]
    metadata = json.loads((SOURCE / "tables.json").read_text(encoding="utf-8-sig"))
    entry = next(item for item in metadata if item.get("name", item.get("table")) == TABLE)
    entry["valid"] = len(rows)
    TARGET.mkdir(parents=True, exist_ok=True)
    with (TARGET / f"{TABLE}.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=header)
        writer.writeheader()
        writer.writerows(rows)
    (TARGET / "tables.json").write_text(json.dumps([entry], indent=2) + "\n", encoding="utf-8")
    print(f"Packaged {len(rows)} NHL 27 equipment rows in {TARGET}")


if __name__ == "__main__":
    main()
