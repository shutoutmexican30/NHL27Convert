using Suite.Core;

namespace Suite.Rosters;

/// <summary>The complete runtime identity of a team helmet/glove row. Team, brand and jersey art are not interchangeable.</summary>
public sealed record EquipmentEditKey(long Team, long Brand, long JerseyArtId, long Equipment, long Era, long Variant)
{
    public bool IsReserved => Variant is 24 or 25;
    public string Type => Equipment == 4 ? "Helmet" : Equipment == 7 ? "Gloves" : $"Type {Equipment}";
    public string Summary => $"{Type} · brand {Brand} · art {JerseyArtId} · era {Era} · variant {Variant}";
}

public sealed record EquipmentEditRow(EquipmentEditKey Key, IReadOnlyDictionary<string, long> Zones);
public sealed record EquipmentDatabaseEditRow(string Database, EquipmentEditRow Row);

/// <summary>A validated, in-memory equipment edit. Apply uses these original/corrected logical payloads, never raw UI bytes.</summary>
public sealed class EquipmentEditPlan
{
    public IReadOnlyDictionary<string, byte[]> OriginalDatabases { get; }
    public IReadOnlyDictionary<string, byte[]> Databases { get; }
    public IReadOnlyList<EquipmentDatabaseEditRow> Rows { get; }
    public IReadOnlyList<EquipmentEditKey> Inserted { get; }
    public string MetaXml { get; }
    public int ChangedRows => Rows.Count - Inserted.Count;
    public bool HasChanges => Databases.Any(kv => !kv.Value.AsSpan().SequenceEqual(OriginalDatabases[kv.Key]));

    internal EquipmentEditPlan(Dictionary<string, byte[]> original, Dictionary<string, byte[]> edited,
        List<EquipmentDatabaseEditRow> rows, List<EquipmentEditKey> inserted, string metaXml)
    {
        OriginalDatabases = original; Databases = edited; Rows = rows; Inserted = inserted; MetaXml = metaXml;
    }

    /// <summary>Independently reopens actual bytes and checks the complete edit plus preservation of every unrelated row.</summary>
    public void Verify(IReadOnlyDictionary<string, byte[]> actual)
    {
        foreach (var (database, expected) in Databases)
        {
            if (!actual.TryGetValue(database, out var bytes) || !bytes.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException(database + ": the logical database payload did not read back exactly.");
            var db = T3Db.Load((byte[])bytes.Clone(), MetaXml);
            foreach (var item in Rows.Where(r => r.Database == database))
            {
                var found = EquipmentEditorDocument.Find(db, item.Row.Key);
                if (found.Count != 1) throw new InvalidDataException($"{database}: {item.Row.Key.Summary}: expected one row, found {found.Count}.");
                foreach (string zone in NewUniformEquipmentPlan.Zones)
                    if (db.GetInt(found[0].Table, found[0].Row, zone) != item.Row.Zones[zone])
                        throw new InvalidDataException($"{database}: {item.Row.Key.Summary}: {zone} did not read back.");
            }
        }
    }
}

/// <summary>
/// A constrained stockteamequipmenttable editor. It can color reserved Fanatics rows (24/25) and explicitly duplicate a
/// donor into those variants. Stock rows, goalie equipment, jerseys and every unrelated table are preserved. A duplicate
/// uses the installed target jersey's art ID, rather than silently carrying a donor's wrong art binding.
/// </summary>
public sealed class EquipmentEditorDocument
{
    public const string PrimaryDatabase = "nhl_game";
    static readonly string[] RequiredDatabases = [PrimaryDatabase, "initial_experience"];
    static readonly string[] KeyFields = ["team", "brand", "jerseyartid", "equipment", "era", "variant"];
    readonly Dictionary<string, T3Db> _working;
    readonly Dictionary<string, byte[]> _original;
    readonly Dictionary<string, List<OriginalRecord>> _snapshots;
    readonly HashSet<EquipmentEditKey> _touched = [];
    readonly HashSet<EquipmentEditKey> _inserted = [];

    EquipmentEditorDocument(Dictionary<string, byte[]> original, string metaXml)
    {
        MetaXml = metaXml;
        _original = original.ToDictionary(kv => kv.Key, kv => (byte[])kv.Value.Clone(), StringComparer.Ordinal);
        _working = original.ToDictionary(kv => kv.Key, kv => T3Db.Load((byte[])kv.Value.Clone(), metaXml), StringComparer.Ordinal);
        _snapshots = _working.ToDictionary(kv => kv.Key, kv => CaptureRecords(kv.Value), StringComparer.Ordinal);
        foreach (var (database, db) in _working)
        {
            var table = Table(db);
            foreach (string field in KeyFields.Append("index").Concat(NewUniformEquipmentPlan.Zones))
                if (!table.Fields.ContainsKey(field)) throw new InvalidDataException($"{database}: equipment field {field} is missing.");
            foreach (var group in Rows(db).Where(r => r.Key.IsReserved).GroupBy(r => r.Key).Where(g => g.Count() != 1))
                throw new InvalidDataException($"{database}: duplicate reserved equipment runtime key: {group.Key.Summary}.");
            // Other leagues and older mods may already use variants 24/25 with multiple styles. Their art bindings
            // are outside this editor; a binding is checked only when that exact key is selected for an edit.
        }
    }

    public string MetaXml { get; }
    public IReadOnlyDictionary<string, byte[]> OriginalDatabases => _original;

    public static EquipmentEditorDocument Load(IReadOnlyDictionary<string, byte[]> databases, string metaXml)
    {
        if (!RequiredDatabases.All(databases.ContainsKey) || databases.Count != RequiredDatabases.Length)
            throw new InvalidDataException("Both nhl_game and initial_experience are required; no other database will be changed.");
        return new EquipmentEditorDocument(databases.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), metaXml);
    }

    public IReadOnlyList<EquipmentEditRow> Rows(int? team = null, int? equipment = null, int? variant = null) =>
        Rows(_working[PrimaryDatabase]).Where(r => (team is null || r.Key.Team == team)
            && (equipment is null || r.Key.Equipment == equipment)
            && (variant is null || r.Key.Variant == variant)).ToList();

    public bool IsPaired(EquipmentEditKey key)
    {
        if (!_working.Values.All(db => Find(db, key).Count == 1)) return false;
        if (!key.IsReserved) return true;
        try { foreach (var db in _working.Values) _ = JerseyArt(db, key.Team, key.Variant, key.JerseyArtId); return true; }
        catch (InvalidDataException) { return false; }
    }

    public void SetZones(EquipmentEditKey key, IReadOnlyDictionary<string, long> zones)
    {
        if (!key.IsReserved) throw new InvalidDataException("Only the new Fanatics variants 24 and 25 can be recolored; stock rows stay untouched.");
        ValidateZones(zones);
        foreach (var db in _working.Values)
        {
            _ = JerseyArt(db, key.Team, key.Variant, key.JerseyArtId);
            var found = Find(db, key);
            if (found.Count != 1) throw new InvalidDataException("The equipment key must occur exactly once in both databases: " + key.Summary);
            var (table, row) = found[0];
            foreach (string zone in NewUniformEquipmentPlan.Zones) db.SetInt(table, row, zone, zones[zone]);
        }
        _touched.Add(key);
    }

    public EquipmentEditKey Duplicate(EquipmentEditKey donor, int targetVariant)
    {
        if (targetVariant is not (24 or 25)) throw new InvalidDataException("Choose a reserved Fanatics target variant: 24 (away) or 25 (home).");
        if (donor.Equipment is not (4 or 7)) throw new InvalidDataException("Only helmets and gloves can be duplicated here.");
        var targetArt = JerseyArt(_working[PrimaryDatabase], donor.Team, targetVariant);
        var target = donor with { JerseyArtId = targetArt, Variant = targetVariant };
        foreach (var (name, db) in _working)
        {
            if (Find(db, donor).Count != 1) throw new InvalidDataException($"{name}: the selected donor is missing or ambiguous.");
            if (Find(db, target).Count != 0) throw new InvalidDataException($"{name}: the target already exists; select it to recolor instead: {target.Summary}.");
            var collision = Rows(db).Where(r => r.Key.Team == target.Team && r.Key.Brand == target.Brand
                && r.Key.Equipment == target.Equipment && r.Key.Era == target.Era && r.Key.Variant == target.Variant).ToList();
            if (collision.Count != 0) throw new InvalidDataException($"{name}: another jersey-art ID already occupies {target.Summary}.");
            if (Table(db).InUse >= Table(db).Slots) throw new InvalidDataException($"{name}: no spare equipment rows remain.");
            _ = JerseyArt(db, donor.Team, targetVariant, targetArt);
        }
        foreach (var db in _working.Values)
        {
            var table = Table(db);
            var block = Enumerable.Range(0, table.InUse).Where(r => db.GetInt(table, r, "team") == donor.Team).ToList();
            if (block.Count == 0) throw new InvalidDataException("The donor's team has no equipment rows.");
            int at = block[^1] + 1;
            int source = Find(db, donor)[0].Row;
            table = db.InsertRecord(table, at, source);
            db.SetInt(table, at, "jerseyartid", targetArt);
            db.SetInt(table, at, "variant", targetVariant);
        }
        _touched.Add(target); _inserted.Add(target);
        return target;
    }

    public EquipmentEditPlan BuildPlan()
    {
        var saved = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, db) in _working)
        {
            var table = Table(db);
            for (int row = 0; row < table.InUse; row++) db.SetInt(table, row, "index", row);
            byte[] bytes = db.Save();
            if (bytes.Length != _original[name].Length) throw new InvalidDataException(name + ": database length changed.");
            saved[name] = (byte[])bytes.Clone();
        }
        var rows = _touched.OrderBy(k => k.Team).ThenBy(k => k.Variant).ThenBy(k => k.Equipment).ThenBy(k => k.Brand)
            .SelectMany(key => _working.Keys.Select(name => new EquipmentDatabaseEditRow(name, Read(_working[name], key))))
            .ToList();
        var plan = new EquipmentEditPlan(_original.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), saved,
            rows, _inserted.ToList(), MetaXml);
        VerifyPreservation(plan, _snapshots);
        plan.Verify(saved);
        return plan;
    }

    public static List<(T3Db.Table Table, int Row)> Find(T3Db db, EquipmentEditKey key)
    {
        var table = Table(db);
        return Enumerable.Range(0, table.InUse).Where(row => Key(db, table, row) == key)
            .Select(row => (table, row)).ToList();
    }

    static string Other(string database) => database == PrimaryDatabase ? RequiredDatabases[1] : PrimaryDatabase;
    static T3Db.Table Table(T3Db db) => NewUniformEquipmentPlan.EquipmentTable(db);

    static List<EquipmentEditRow> Rows(T3Db db)
    {
        var table = Table(db);
        return Enumerable.Range(0, table.InUse).Select(row => Read(db, table, row))
            .Where(r => r.Key.Equipment is 4 or 7).ToList();
    }

    static EquipmentEditRow Read(T3Db db, EquipmentEditKey key)
    {
        var found = Find(db, key);
        if (found.Count != 1) throw new InvalidDataException("Missing or ambiguous equipment key: " + key.Summary);
        return Read(db, found[0].Table, found[0].Row);
    }

    static EquipmentEditRow Read(T3Db db, T3Db.Table table, int row) =>
        new(Key(db, table, row), NewUniformEquipmentPlan.Zones.ToDictionary(z => z, z => db.GetInt(table, row, z), StringComparer.Ordinal));

    static EquipmentEditKey Key(T3Db db, T3Db.Table table, int row) =>
        new(db.GetInt(table, row, "team"), db.GetInt(table, row, "brand"), db.GetInt(table, row, "jerseyartid"),
            db.GetInt(table, row, "equipment"), db.GetInt(table, row, "era"), db.GetInt(table, row, "variant"));

    static long JerseyArt(T3Db db, long team, long variant, long? expected = null)
    {
        if (!db.Tables.TryGetValue(JerseyRowPlanner.TableName, out var jerseys)) throw new InvalidDataException("stockteamjerseys is missing.");
        var art = Enumerable.Range(0, jerseys.InUse).Where(row => db.GetInt(jerseys, row, "teamid") == team
                && db.GetInt(jerseys, row, "variant") == variant)
            .Select(row => db.GetInt(jerseys, row, "teamartid")).Distinct().ToList();
        if (art.Count != 1) throw new InvalidDataException($"Team {team}, variant {variant}: expected one installed jersey-art ID, found {art.Count}.");
        if (expected is not null && art[0] != expected) throw new InvalidDataException(
            $"Team {team}, variant {variant}: equipment art {expected} does not match installed jersey art {art[0]}.");
        return art[0];
    }

    static void ValidateZones(IReadOnlyDictionary<string, long> zones)
    {
        foreach (string zone in NewUniformEquipmentPlan.Zones)
            if (!zones.TryGetValue(zone, out long value) || value is < 0 or > 0xFFFFFF)
                throw new InvalidDataException($"{zone}: enter a six-digit RGB color from 000000 to FFFFFF.");
        if (zones.Count != 15) throw new InvalidDataException("Exactly 15 equipment color zones are required.");
    }

    sealed record OriginalRecord(EquipmentEditKey Key, byte[] Bytes);

    static List<OriginalRecord> CaptureRecords(T3Db db)
    {
        var table = Table(db);
        return Enumerable.Range(0, table.InUse).Select(row => new OriginalRecord(Key(db, table, row),
            db.Bytes.AsSpan(table.RecordsAt + row * table.RecordBytes, table.RecordBytes).ToArray())).ToList();
    }

    static void VerifyPreservation(EquipmentEditPlan plan, IReadOnlyDictionary<string, List<OriginalRecord>> originals)
    {
        var touched = plan.Rows.Select(r => r.Row.Key).ToHashSet();
        var inserted = plan.Inserted.ToHashSet();
        foreach (var (name, bytes) in plan.Databases)
        {
            var beforeBytes = plan.OriginalDatabases[name];
            var before = T3Db.Load((byte[])beforeBytes.Clone(), plan.MetaXml);
            var after = T3Db.Load((byte[])bytes.Clone(), plan.MetaXml);
            foreach (var table in after.Tables.Values.Where(t => t.Name != NewUniformEquipmentPlan.TableName))
                if (!beforeBytes.AsSpan(table.Start, table.End - table.Start).SequenceEqual(bytes.AsSpan(table.Start, table.End - table.Start)))
                    throw new InvalidDataException($"{name}: unrelated table changed: {table.Name}.");
            var snapshots = originals[name];
            var at = Table(after); int original = 0;
            for (int row = 0; row < at.InUse; row++)
            {
                var key = Key(after, at, row);
                var raw = after.Bytes.AsSpan(at.RecordsAt + row * at.RecordBytes, at.RecordBytes).ToArray();
                if (original < snapshots.Count && snapshots[original].Key == key)
                {
                    var old = (byte[])snapshots[original].Bytes.Clone();
                    Clear(after.Tables[NewUniformEquipmentPlan.TableName], raw, "index");
                    Clear(before.Tables[NewUniformEquipmentPlan.TableName], old, "index");
                    if (touched.Contains(key) && key.IsReserved)
                    {
                        foreach (string zone in NewUniformEquipmentPlan.Zones)
                        {
                            Clear(at, raw, zone); Clear(Table(before), old, zone);
                        }
                    }
                    if (!raw.AsSpan().SequenceEqual(old)) throw new InvalidDataException($"{name}: an unrelated field or stock row changed at {key.Summary}.");
                    original++;
                }
                else if (!inserted.Contains(key) || !key.IsReserved)
                    throw new InvalidDataException($"{name}: unexpected insertion, removal or reordering at {key.Summary}.");
            }
            if (original != snapshots.Count) throw new InvalidDataException($"{name}: not every original equipment row was preserved.");
        }
    }

    static void Clear(T3Db.Table table, byte[] record, string field)
    {
        var f = table.Fields[field];
        for (int bit = 0; bit < f.Depth; bit++)
        {
            int at = f.BitOffset + bit;
            record[at >> 3] &= (byte)~(1 << (at & 7));
        }
    }
}
