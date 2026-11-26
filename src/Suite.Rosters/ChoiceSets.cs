using System.Text;

namespace Suite.Rosters;

/// <summary>
/// The named values of the fields that pick from a list (position, archetype, hair style, helmet...). A few are fixed here; the rest are read
/// from the game's own create-a-player tables (gamemodes/creation_zone_csv/*.csv, the same files Blueline Studio reads for NHL 19), with
/// names resolved through the language table when it is given. A field value no list names is still shown as its number.
/// </summary>
public sealed class ChoiceSets
{
    public sealed record Choice(long Value, string Name)
    {
        public override string ToString() => Name;
    }

    public const string Position = "position", PositionPrimary = "positionprimary", SkaterStyle = "skaterstyle", GoalieStyle = "goaliestyle", Hand = "hand",
        Fighting = "fighting", Captain = "captain", ContractType = "contracttype", Origin = "origin", Trait = "trait", Celebration = "celebration",
        SkatingStyle = "skatingstyle", Stance = "stance", HairColour = "haircolour", HairStyle = "hairstyle", EyeColour = "eyecolour", Helmet = "helmet",
        Gloves = "gloves", Stick = "stick", Skates = "skates", VisorTint = "visortint", JerseyTuck = "jerseytuck", Pants = "pants", Team = "team";

    public const string CsvFolder = "gamemodes/creation_zone_csv/";

    readonly Dictionary<string, IReadOnlyList<Choice>> _sets = new(StringComparer.Ordinal);

    public ChoiceSets()
    {
        Put(Position, [(0, "Centre"), (1, "Left wing"), (2, "Right wing"), (3, "Defence"), (4, "Goalie")]);
        Put(PositionPrimary, [(-1, "None"), (0, "Centre"), (1, "Left wing"), (2, "Right wing"), (3, "Left defence"), (4, "Right defence"), (5, "Goalie")]);
        Put(SkaterStyle, [(1, "Defensive defenceman"), (2, "Offensive defenceman"), (3, "Enforcer defenceman"), (4, "Two-way defenceman"), (5, "Grinder"),
            (6, "Playmaker"), (7, "Sniper"), (8, "Power forward"), (9, "Two-way forward"), (10, "Enforcer")]);
        Put(GoalieStyle, [(0, "Stand-up"), (1, "Hybrid"), (2, "Butterfly")]);
        Put(Hand, [(0, "Left"), (1, "Right")]);
        Put(Fighting, [(0, "Never"), (1, "Rarely"), (2, "Sometimes"), (3, "Often")]);
        Put(Captain, [(0, "None"), (1, "Captain"), (2, "Alternate")]);
        Put(ContractType, [(0, "One-way"), (1, "Two-way")]);
    }

    public IReadOnlyList<Choice>? Get(string? name) => name != null && _sets.TryGetValue(name, out var set) ? set : null;

    public string NameOf(string? set, long value) => Get(set)?.FirstOrDefault(c => c.Value == value)?.Name ?? value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public void Put(string name, IEnumerable<(long Value, string Name)> values) => _sets[name] = values.Select(v => new Choice(v.Value, v.Name)).ToList();

    /// <summary>
    /// Reads the CSV-backed lists. <paramref name="read"/> returns a legacy game file's bytes (null when absent); <paramref name="loc"/> turns a
    /// "$KEY" name into its text (null: prettify the key). <paramref name="teams"/> fills the team list.
    /// </summary>
    public static ChoiceSets Load(Func<string, byte[]?> read, Func<string, string?>? loc = null, IEnumerable<(long Value, string Name)>? teams = null)
    {
        var sets = new ChoiceSets();
        string Name(string text)
        {
            if (!text.StartsWith('$')) return text;
            var key = text[1..];
            if (loc?.Invoke(key) is { Length: > 0 } found) return found;
            key = key.StartsWith("TXT_", StringComparison.OrdinalIgnoreCase) ? key[4..] : key;
            return string.Join(' ', key.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Length > 1 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w));
        }
        void FromCsv(string set, string file, string valueColumn, string nameColumn, Func<Dictionary<string, string>, string>? label = null)
        {
            if (read(CsvFolder + file) is not { } bytes) return;
            var rows = Csv(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
            if (rows.Count < 2) return;
            var header = rows[0].Select(h => h.Trim()).ToList();
            var values = new List<(long, string)>(); var seen = new HashSet<long>();
            foreach (var cells in rows.Skip(1))
            {
                var map = header.Select((h, i) => (h, v: i < cells.Count ? cells[i].Trim() : "")).ToDictionary(p => p.h, p => p.v, StringComparer.OrdinalIgnoreCase);
                if (!map.TryGetValue(valueColumn, out var raw) || !long.TryParse(raw, out var value) || !seen.Add(value)) continue;
                values.Add((value, label?.Invoke(map) ?? Name(map.GetValueOrDefault(nameColumn, raw))));
            }
            if (values.Count > 0) sets.Put(set, values);
        }
        FromCsv(Origin, "origins.csv", "index", "name");
        FromCsv(Celebration, "gamecelebrations.csv", "animationid", "name");
        FromCsv(SkatingStyle, "skaterskatingstyles.csv", "animationid", "name", m => $"Style {long.Parse(m["animationid"]) + 1}");
        FromCsv(Stance, "goaliestances.csv", "animationid", "name", m => $"Stance {long.Parse(m["animationid"]) + 1}");
        FromCsv(HairColour, "appearancehaircolors.csv", "assetid", "name");
        FromCsv(HairStyle, "appearancehairstyles.csv", "assetid", "name", m => $"Hair style {m.GetValueOrDefault("nameindex", m["assetid"])}");
        FromCsv(EyeColour, "appearanceeyecolors.csv", "assetid", "colorname");
        FromCsv(Helmet, "skaterhelmetstyle.csv", "assetid", "itemname");
        FromCsv(Gloves, "skaterglovestyle.csv", "assetid", "itemname");
        FromCsv(Stick, "stickdisplayinfo.csv", "stickassetid", "stickname");
        FromCsv(Skates, "skatedisplayinfo.csv", "skateassetid", "skatename");
        FromCsv(VisorTint, "skatervisortint.csv", "assetid", "name");
        FromCsv(JerseyTuck, "skaterjerseytuck.csv", "assetid", "name");
        FromCsv(Pants, "playerpantlength.csv", "assetid", "name");
        if (read(CsvFolder + "playertraits.csv") is { } traitBytes)
        {
            var rows = Csv(Encoding.UTF8.GetString(traitBytes).TrimStart('﻿'));
            if (rows.Count > 1)
            {
                int id = rows[0].IndexOf("traitid"), name = rows[0].IndexOf("trait");
                var list = new List<(long, string)> { (0, "None") };
                if (id >= 0 && name >= 0)
                    foreach (var cells in rows.Skip(1).Where(c => c.Count > Math.Max(id, name)))
                        if (long.TryParse(cells[id], out var v) && list.All(l => l.Item1 != v))
                        {
                            var lower = cells[name].Trim().ToLowerInvariant();
                            list.Add((v, lower.Length == 0 ? lower : char.ToUpperInvariant(lower[0]) + lower[1..]));
                        }
                sets.Put(Trait, list.OrderBy(l => l.Item1));
            }
        }
        if (teams != null) sets.Put(Team, teams);
        return sets;
    }

    /// <summary>Minimal CSV reader: comma separated, double quotes escape commas, quotes and line breaks.</summary>
    public static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = false; }
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = [];
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
