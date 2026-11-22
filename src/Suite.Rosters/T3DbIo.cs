using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Suite.Rosters;

/// <summary>
/// The suite's own db-dump / db-import / db-layout for NHL 24's T3DB roster databases, replacing the stopgap
/// NHL24Kit Worker's commands (its runtime is no longer needed for the roster sync). Same shapes end to end:
/// a dump folder is one CSV per table (one row per record slot, blanks included) plus tables.json; an import
/// rebuilds the edited tables inside the stock file; a layout report feeds <see cref="RosterDbJob.Guard"/>.
/// The file size, the table directory, every table's record slots and record sizes never change; only record
/// contents, a table's in-use count and the CRC values may move.
/// </summary>
public static class T3DbIo
{
    // ---------------------------------------------------------------- raw record access (no in-use guards)

    static Span<byte> Record(T3Db.Table t, byte[] bytes, int r) =>
        bytes.AsSpan(t.RecordsAt + r * t.RecordBytes, t.RecordBytes);

    static bool IsBlank(T3Db.Table t, byte[] bytes, int r) => Record(t, bytes, r).IndexOfAnyExcept((byte)0) < 0;

    /// <summary>Leading record slots whose packed bits are not all zero (what db-dump reports as "valid").</summary>
    static int LeadingUsed(T3Db.Table t, byte[] bytes)
    {
        int n = 0;
        while (n < t.Slots && !IsBlank(t, bytes, n)) n++;
        return n;
    }

    static string ReadString(T3Db.Field f, ReadOnlySpan<byte> record)
    {
        var span = record.Slice(f.BitOffset / 8, f.Depth / 8);
        int end = span.IndexOf((byte)0);
        if (end >= 0) span = span[..end];
        return Encoding.UTF8.GetString(span);
    }

    static void WriteString(T3Db.Table t, T3Db.Field f, Span<byte> record, string value)
    {
        if (f.BitOffset % 8 != 0 || f.Depth % 8 != 0) throw new InvalidDataException($"{t.Name}.{f.Name}: string not byte aligned.");
        var text = Encoding.UTF8.GetBytes(value);
        int width = f.Depth / 8;
        if (text.Length + 1 > width && text.Length != width)
            throw new ArgumentOutOfRangeException(nameof(value), $"{t.Name}.{f.Name}: '{value}' needs {text.Length + 1} bytes, the field holds {width}.");
        var span = record.Slice(f.BitOffset / 8, width);
        span.Clear();
        text.CopyTo(span);
    }

    static void WriteInt(T3Db.Table t, T3Db.Field f, Span<byte> record, long value)
    {
        long raw = value - f.RangeLow;
        if (raw < 0 || (f.Depth < 63 && raw >= 1L << f.Depth))
            throw new ArgumentOutOfRangeException(nameof(value), $"{t.Name}.{f.Name}: {value} does not fit {f.Depth} bits from {f.RangeLow}.");
        for (int b = 0; b < f.Depth; b++)
        {
            int bit = f.BitOffset + b, p = bit >> 3, m = 1 << (bit & 7);
            record[p] = (byte)(((raw >> b) & 1) != 0 ? record[p] | m : record[p] & ~m);
        }
    }

    static void SetInUse(T3Db.Table t, byte[] bytes, int used)
    {
        uint head = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(t.Start + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(t.Start + 16), ((uint)used << 16) | (head & 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(t.Start + 32), T3Db.Crc(bytes.AsSpan(t.Start, 32)));
    }

    /// <summary>
    /// Repair for databases written before the Import fix: every table whose header counts more in-use records than
    /// it has leading non-empty slots, with only empty (all-zero) slots in between, gets its in-use count set back to
    /// the leading count. Nothing else changes. Returns (table, before, after) per repaired table.
    /// </summary>
    public static List<(string Table, int Before, int After)> RepairInUse(byte[] bytes, string metaXml)
    {
        var db = T3Db.Load(bytes, metaXml);
        var fixedTables = new List<(string, int, int)>();
        foreach (var t in Ordered(db))
        {
            int leading = LeadingUsed(t, bytes);
            if (t.InUse <= leading) continue;
            bool onlyEmpty = true;
            for (int r = leading; r < t.InUse && onlyEmpty; r++) onlyEmpty = IsBlank(t, bytes, r);
            if (!onlyEmpty) continue; // a real gap with data after it: leave it for a person to look at
            SetInUse(t, bytes, leading);
            fixedTables.Add((t.Name, t.InUse, leading));
        }
        if (fixedTables.Count > 0) T3Db.Load(bytes, metaXml); // every CRC must still hold
        return fixedTables;
    }

    static void RewriteSectionCrc(T3Db.Table t, byte[] bytes) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(t.End - 4), T3Db.Crc(bytes.AsSpan(t.Start + 36, t.End - 4 - (t.Start + 36))));

    static IEnumerable<T3Db.Table> Ordered(T3Db db) => db.Tables.Values.OrderBy(t => t.Start);

    // ---------------------------------------------------------------- db-dump

    /// <summary>
    /// Writes every table of <paramref name="dbPath"/> as a CSV (one row per record slot; integers as numbers,
    /// strings up to the first NUL, blank slots as their lowest values) plus tables.json
    /// ([{ table, shortName, records, valid, max, fields }]). Returns the tables.json text.
    /// </summary>
    public static string Dump(string dbPath, string metaXml, string outDir)
    {
        var bytes = File.ReadAllBytes(dbPath);
        var db = T3Db.Load(bytes, metaXml);
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);
        var tables = new List<Dictionary<string, object>>();
        foreach (var t in Ordered(db))
        {
            var fields = t.Fields.Values.OrderBy(f => f.BitOffset).ToList();
            var header = fields.Select(f => f.Name).ToArray();
            var sb = new StringBuilder();
            sb.Append(string.Join(",", header.Select(Quote))).Append("\r\n");
            for (int r = 0; r < t.Slots; r++)
            {
                var rec = Record(t, bytes, r);
                var cells = new string[fields.Count];
                for (int c = 0; c < fields.Count; c++)
                {
                    var f = fields[c];
                    cells[c] = Quote(f.Type == 0 ? ReadString(f, rec) : db.GetInt(t, r, f.Name).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.Append(string.Join(",", cells)).Append("\r\n");
            }
            File.WriteAllText(Path.Combine(outDir, t.Name + ".csv"), sb.ToString(), new UTF8Encoding(true));
            tables.Add(new Dictionary<string, object>
            {
                ["table"] = t.Name, ["shortName"] = t.Short, ["records"] = t.Slots,
                ["valid"] = LeadingUsed(t, bytes), ["max"] = t.Slots, ["fields"] = header,
            });
        }
        var json = JsonSerializer.Serialize(tables);
        File.WriteAllText(Path.Combine(outDir, "tables.json"), json);
        return json;
    }

    // ---------------------------------------------------------------- db-layout

    /// <summary>
    /// The layout facts <see cref="RosterDbJob.Guard"/> compares: table order, record slots, record sizes and where
    /// each table's records CRC would sit if it followed the last in-use record (so a changed in-use count shows up
    /// as exactly in-use delta x record size).
    /// </summary>
    public static string Layout(string dbPath, string metaXml)
    {
        var bytes = File.ReadAllBytes(dbPath);
        var db = T3Db.Load(bytes, metaXml);
        var list = Ordered(db).Select(t => new Dictionary<string, object>
        {
            ["table"] = t.Name,
            ["headerCrcAt"] = (long)t.Start + 32,
            ["records"] = t.Slots,
            ["recordSize"] = t.RecordBytes,
            ["strings"] = t.Fields.Values.Where(f => f.Type == 0).Sum(f => f.Depth / 8),
            ["recordsCrcAt"] = (long)t.RecordsAt + (long)LeadingUsed(t, bytes) * t.RecordBytes,
        }).ToList();
        return JsonSerializer.Serialize(list);
    }

    // ---------------------------------------------------------------- db-import

    /// <summary>
    /// Rebuilds <paramref name="beforeDbPath"/> with the edited table CSVs of <paramref name="editDir"/> (compared
    /// against the unedited dump in <paramref name="beforeDir"/>) and writes the result to <paramref name="outDb"/>.
    /// Every edited table keeps its record slots, but its rows are rewritten in full from the CSV, in CSV order —
    /// the new-player step may insert rows, so a row's position in the file is not its position in the edited table.
    /// Spare slots are zeroed, the header's in-use count follows the CSV's valid count, and both CRCs are
    /// recomputed. A table whose CSV row count is not the slot count, an unknown field, or a value that does not
    /// fit its field throws instead of writing a bad file. The result is re-loaded afterwards so any CRC mistake
    /// fails here, not in the game. Returns the number of changed cells per table (against the unedited dump).
    /// </summary>
    public static Dictionary<string, int> Import(string beforeDbPath, string metaXml, string beforeDir, string editDir, string outDb)
    {
        var bytes = File.ReadAllBytes(beforeDbPath);
        var db = T3Db.Load(bytes, metaXml);
        var written = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(editDir, "*.csv"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!db.Tables.TryGetValue(name, out var t)) throw new InvalidOperationException($"{name}: not in this database.");
            var before = T3Csv.Load(beforeDir, name);
            var edit = T3Csv.Load(editDir, name);
            if (edit.Rows.Count != t.Slots)
                throw new InvalidOperationException($"{name}: {edit.Rows.Count} rows, the table has {t.Slots} slots; a table may never grow or shrink.");
            foreach (var f in t.Fields.Values)
                if (!edit.Has(f.Name)) throw new InvalidOperationException($"{name}: the edited CSV has no {f.Name} column.");
            int used = edit.ValidCount;
            if (used > t.Slots) throw new InvalidOperationException($"{name}: {used} in-use rows do not fit in {t.Slots} slots.");

            int cells = 0;
            for (int r = 0; r < t.Slots; r++)
            {
                var a = before.Rows[r];
                var b = edit.Rows[r];
                var rec = Record(t, bytes, r);
                rec.Clear();
                foreach (var f in t.Fields.Values)
                {
                    var value = edit.Get(b, f.Name);
                    if (f.Type == 0) WriteString(t, f, rec, value);
                    else if (f.Type == 3) WriteInt(t, f, rec, long.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                    else throw new InvalidOperationException($"{name}.{f.Name}: unsupported field type {f.Type}.");
                    if (before.Get(a, f.Name) != value) cells++;
                }
            }
            // The edit CSVs have no tables.json, so ValidCount falls back to T3Csv.IsBlank, which only knows "0"/"".
            // A spare slot reads back as each field's lowest value (friendenemycompat0 = -256), so without this the
            // in-use count became the whole table (2026-11-22: 13,300 players, 4,178 of them empty slots). The written
            // records are the truth: a slot whose packed bits are all zero is spare, exactly as db-dump counts them.
            used = Math.Min(used, LeadingUsed(t, bytes));
            for (int r = used; r < t.Slots; r++)
                if (!IsBlank(t, bytes, r)) throw new InvalidOperationException($"{name}: slot {r} holds data after the {used} in-use records.");
            if (used != t.InUse) SetInUse(t, bytes, used);
            RewriteSectionCrc(t, bytes);
            written[name] = cells;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outDb))!);
        File.WriteAllBytes(outDb, bytes);
        T3Db.Load(File.ReadAllBytes(outDb), metaXml);   // every CRC must hold before the file leaves dbwork
        return written;
    }

    static string Quote(string s) => s.IndexOfAny([',', '"', '\n', '\r']) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
}
