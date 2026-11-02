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
        return new T3Csv(table, header, body);
    }

    public void Save(string dir)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Header.Select(Quote))).Append("\r\n");
        foreach (var r in Rows) sb.Append(string.Join(",", r.Select(Quote))).Append("\r\n");
        Directory.CreateDirectory(dir);
        File.WriteAllText(PathOf(dir, Name), sb.ToString(), new UTF8Encoding(true));
    }

    public bool Has(string col) => _cols.ContainsKey(col);
    public int Col(string col) => _cols.TryGetValue(col, out var i) ? i : throw new KeyNotFoundException($"{Name} has no column {col}.");
    public string Get(string[] row, string col) => row[Col(col)];
    public int Int(string[] row, string col) => int.TryParse(row[Col(col)], out var v) ? v : 0;
    public void Set(string[] row, string col, string value) => row[Col(col)] = value;
    public void Set(string[] row, string col, int value) => row[Col(col)] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A record slot nobody uses: every number 0 and every string empty (what the database writer treats as clean).</summary>
    public static bool IsBlank(string[] row) => row.All(c => c.Length == 0 || c == "0");

    /// <summary>In-use records sit together at the front; this is how many there are.</summary>
    public int ValidCount
    {
        get
        {
            int n = 0;
            while (n < Rows.Count && !IsBlank(Rows[n])) n++;
            return n;
        }
    }

    public string[] BlankRow() => Header.Select(_ => "0").ToArray();

    /// <summary>
    /// Replaces the in-use records with <paramref name="used"/> (in that order) and pads with blank slots so the table
    /// keeps exactly as many record slots as before. Throws instead of growing the table.
    /// </summary>
    public void ReplaceUsed(List<string[]> used)
    {
        int slots = Rows.Count;
        if (used.Count > slots) throw new InvalidOperationException($"{Name}: {used.Count} records do not fit in {slots} slots.");
        if (used.Any(IsBlank)) throw new InvalidOperationException($"{Name}: an in-use record would be blank and get dropped.");
        var blank = Rows.Skip(ValidCount).FirstOrDefault(IsBlank) ?? BlankRow();
        Rows.Clear();
        Rows.AddRange(used);
        while (Rows.Count < slots) Rows.Add((string[])blank.Clone());
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
