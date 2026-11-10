using System.Text;

namespace Suite.Rosters;

/// <summary>
/// One database table as a CSV dump (the stopgap Worker's db-dump / db-import format: UTF-8 with BOM, a header row,
/// one row per record slot including the blank ones). Cells stay strings, so anything the sync does not touch is
/// written back exactly as it was read.
/// </summary>
public sealed class T3Csv
{
    public string Name { get; }
    public string[] Header { get; }
    public List<string[]> Rows { get; }
    readonly Dictionary<string, int> _cols;
    int? _valid;

    T3Csv(string name, string[] header, List<string[]> rows)
    {
        Name = name;
        Header = header;
        Rows = rows;
        _cols = header.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i);
    }

    public static string PathOf(string dir, string table) => System.IO.Path.Combine(dir, table + ".csv");

    public static T3Csv Load(string dir, string table)
    {
        var text = File.ReadAllText(PathOf(dir, table), Encoding.UTF8);
        var rows = Parse(text);
        if (rows.Count == 0) throw new InvalidDataException($"{table}.csv is empty.");
        var header = rows[0].Select(h => h.TrimStart('﻿')).ToArray();
        var body = rows.Skip(1).Where(r => !(r.Length == 1 && r[0].Length == 0)).ToList();
        foreach (var r in body)
            if (r.Length != header.Length) throw new InvalidDataException($"{table}.csv: a row has {r.Length} cells, the header {header.Length}.");
        var t = new T3Csv(table, header, body);
        // The database header's in-use count (db-dump's tables.json next to the CSVs) is the truth: an unused slot is a
        // record whose packed bits are all zero, which reads back as each field's lowest value (-1 for many fields).
        var tables = Path.Combine(dir, "tables.json");
        if (File.Exists(tables))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(tables));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                // NHL 24 db-dump calls the table key "table"; the NHL 27 full dump calls it "name".
                string? name = e.TryGetProperty("table", out var key) ? key.GetString() : e.GetProperty("name").GetString();
                if (name == table && e.TryGetProperty("valid", out var v)) t._valid = v.GetInt32();
            }
        }
        return t;
    }

    public void Save(string dir)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Header.Select(Quote))).Append("\r\n");
        foreach (var r in Rows) sb.Append(string.Join(",", r.Select(Quote))).Append("\r\n");
        Directory.CreateDirectory(dir);
        File.WriteAllText(PathOf(dir, Name), sb.ToString(), new UTF8Encoding(true));
        // Saving back into a dump folder keeps its tables.json in-use count in step with the rows.
        var tables = Path.Combine(dir, "tables.json");
        if (_valid is { } valid && File.Exists(tables))
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(tables))!.AsArray();
            foreach (var e in root)
                if ((string?)e!["table"] == Name) e["valid"] = valid;
            File.WriteAllText(tables, root.ToJsonString());
        }
    }

    public bool Has(string col) => _cols.ContainsKey(col);
    public int Col(string col) => _cols.TryGetValue(col, out var i) ? i : throw new KeyNotFoundException($"{Name} has no column {col}.");
    public string Get(string[] row, string col) => row[Col(col)];
    public int Int(string[] row, string col) => int.TryParse(row[Col(col)], out var v) ? v : 0;
    public void Set(string[] row, string col, string value) => row[Col(col)] = value;
    public void Set(string[] row, string col, int value) => row[Col(col)] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A record slot nobody uses: every number 0 and every string empty (what the database writer treats as clean).</summary>
    public static bool IsBlank(string[] row) => row.All(c => c.Length == 0 || c == "0");

    /// <summary>
    /// In-use records sit together at the front; this is how many there are: the dump's own count when the CSV came
    /// from db-dump (tables.json beside it), else the leading rows that are not all zeros.
    /// </summary>
    public int ValidCount
    {
        get
        {
            if (_valid is { } v) return Math.Min(v, Rows.Count);
            int n = 0;
            while (n < Rows.Count && !IsBlank(Rows[n])) n++;
            return n;
        }
    }

    /// <summary>What an unused slot of this table looks like (the first one after the in-use records).</summary>
    public string[] Filler => ValidCount < Rows.Count ? (string[])Rows[ValidCount].Clone() : BlankRow();

    public string[] BlankRow() => Header.Select(_ => "0").ToArray();

    /// <summary>
    /// Replaces the in-use records with <paramref name="used"/> (in that order) and pads with blank slots so the table
    /// keeps exactly as many record slots as before. Throws instead of growing the table.
    /// </summary>
    public void ReplaceUsed(List<string[]> used)
    {
        int slots = Rows.Count;
        if (used.Count > slots) throw new InvalidOperationException($"{Name}: {used.Count} records do not fit in {slots} slots.");
        var filler = Filler;
        if (used.Any(r => IsBlank(r) || r.SequenceEqual(filler))) throw new InvalidOperationException($"{Name}: an in-use record would look unused and get dropped.");
        Rows.Clear();
        Rows.AddRange(used);
        while (Rows.Count < slots) Rows.Add((string[])filler.Clone());
        _valid = used.Count;
    }

    static string Quote(string s) => s.IndexOfAny([',', '"', '\n', '\r']) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

    static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\n':
                    row.Add(cell.ToString().TrimEnd('\r')); cell.Clear();
                    rows.Add(row.ToArray()); row = [];
                    break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
