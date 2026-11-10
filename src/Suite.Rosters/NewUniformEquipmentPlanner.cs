using Suite.Core;

namespace Suite.Rosters;

/// <summary>One NHL 27 equipment row retained as evidence for a donor decision.</summary>
public sealed record EquipmentSourceCandidate(
    int SourceOrder, long Index, long Team, long Brand, long JerseyArtId, long Equipment, long Era, long Variant,
    IReadOnlyDictionary<string, long> Zones);

/// <summary>A variant-specific equipment row planned for one new home/away uniform.</summary>
public sealed record PlannedEquipmentRow(
    string Database, int Team, string TeamName, string Kit, int SourceVariant, int TargetVariant,
    long Brand, long JerseyArtId, long Equipment, long Era,
    IReadOnlyDictionary<string, long> Zones, bool AlreadyExists,
    EquipmentSourceCandidate Source, IReadOnlyList<EquipmentSourceCandidate> Candidates)
{
    public bool UsesWildcard => Source.Variant == -1;
    public bool UsesDefault => Source.Era == 2 && Source.Variant is 0 or 1 && Source.Variant != SourceVariant;
    public string Selection => UsesWildcard ? "current-era wildcard"
        : UsesDefault ? "current-era default fallback"
        : Source.Era == 2 ? "current-era exact" : "supported brand-specific fallback";
    public string Key => $"team {Team}, brand {Brand}, art {JerseyArtId}, equipment {Equipment}, era {Era}, variant {TargetVariant}";
}

/// <summary>Validated same-length database edits. No texture, stock jersey, or goalie-equipment row is changed.</summary>
public sealed class NewUniformEquipmentPlan
{
    public IReadOnlyDictionary<string, byte[]> Databases { get; }
    public IReadOnlyList<PlannedEquipmentRow> Rows { get; }
    public int InsertedRows => Rows.Count(r => !r.AlreadyExists);
    public IReadOnlyList<string> ChangedDatabases { get; }

    internal NewUniformEquipmentPlan(Dictionary<string, byte[]> databases, List<PlannedEquipmentRow> rows)
    {
        Databases = databases;
        Rows = rows;
        ChangedDatabases = databases.Where(kv => Rows.Any(r => r.Database == kv.Key)).Select(kv => kv.Key).ToArray();
    }

    /// <summary>Reads the installed database and verifies every intended row, key, and color zone.</summary>
    public void Verify(string database, T3Db db)
    {
        var expected = Rows.Where(r => r.Database == database).ToList();
        if (expected.Count == 0) return;
        var table = EquipmentTable(db);
        for (int row = 0; row < table.InUse; row++)
            if (db.GetInt(table, row, "index") != row)
                throw new InvalidDataException($"{database}: equipment index {row} is {db.GetInt(table, row, "index")}.");
        foreach (var item in expected)
        {
            var matches = Enumerable.Range(0, table.InUse).Where(row => KeyMatches(db, table, row, item)).ToList();
            if (matches.Count != 1) throw new InvalidDataException($"{database}: expected one {item.Key}; found {matches.Count}.");
            foreach (var (zone, color) in item.Zones)
                if (db.GetInt(table, matches[0], zone) != color)
                    throw new InvalidDataException($"{database}: {item.Key}, {zone} is #{db.GetInt(table, matches[0], zone):X6}, expected #{color:X6}.");
        }
    }

    internal static T3Db.Table EquipmentTable(T3Db db) => db.Tables.TryGetValue(TableName, out var table)
        ? table : throw new InvalidDataException(TableName + " is missing.");

    internal static bool KeyMatches(T3Db db, T3Db.Table table, int row, PlannedEquipmentRow item) =>
        db.GetInt(table, row, "team") == item.Team && db.GetInt(table, row, "brand") == item.Brand
        && db.GetInt(table, row, "jerseyartid") == item.JerseyArtId && db.GetInt(table, row, "equipment") == item.Equipment
        && db.GetInt(table, row, "era") == item.Era && db.GetInt(table, row, "variant") == item.TargetVariant;

    public const string TableName = "stockteamequipmenttable";
    public static readonly string[] Zones = Enumerable.Range(1, 15).Select(i => $"zone{i}").ToArray();
}

/// <summary>
/// Selects the numbered NHL 27 source kit, not the NHL 24 structural donor. Era 2 exact rows win; era 2 variant=-1 is
/// the wildcard. Older exact alternates (notably Toronto green) never outrank those rows. Ambiguous same-brand candidates
/// remain in the report and are resolved deterministically in source order, never copied as duplicate runtime keys.
/// </summary>
public static class NewUniformEquipmentPlanner
{
    const string Jerseys = JerseyRowPlanner.TableName;
    static readonly int[] EquipmentTypes = [4, 7];

    public static NewUniformEquipmentPlan Build(
        IReadOnlyDictionary<string, byte[]> sourceDatabases, string metaXml, string source27CsvDirectory,
        IEnumerable<NewUniformAddition> additions)
    {
        var table27 = T3Csv.Load(source27CsvDirectory, NewUniformEquipmentPlan.TableName);
        var source = table27.Rows.Take(table27.ValidCount).Select((row, order) => Candidate(table27, row, order))
            .Where(r => r.Team >= 0 && EquipmentTypes.Contains((int)r.Equipment)).ToList();
        return Build(sourceDatabases, metaXml, source, additions);
    }

    internal static NewUniformEquipmentPlan Build(
        IReadOnlyDictionary<string, byte[]> sourceDatabases, string metaXml,
        IReadOnlyList<EquipmentSourceCandidate> source27, IEnumerable<NewUniformAddition> additions)
    {
        var wanted = additions.OrderBy(a => a.TeamId).ThenBy(a => a.SourceKit, StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) throw new InvalidDataException("No uniforms were supplied for equipment planning.");
        var identities = new HashSet<(int Team, int Variant)>();
        foreach (var addition in wanted)
        {
            if (addition.TargetVariant is not (24 or 25)) throw new InvalidDataException(
                $"{addition.Team}: equipment correction is restricted to new variants 24 and 25.");
            if (!identities.Add((addition.TeamId, addition.TargetVariant)))
                throw new InvalidDataException($"{addition.Team}: duplicate target variant {addition.TargetVariant}.");
            if (string.IsNullOrWhiteSpace(addition.SourceFolder27)) throw new InvalidDataException(
                $"{addition.Team} {addition.SourceKit}: the numbered NHL 27 source folder is required.");
        }
        if (sourceDatabases.Count == 0) throw new InvalidDataException("No target databases were supplied.");

        var loaded = sourceDatabases.ToDictionary(kv => kv.Key, kv => T3Db.Load((byte[])kv.Value.Clone(), metaXml), StringComparer.Ordinal);
        var originals = loaded.ToDictionary(kv => kv.Key, kv => Snapshot(kv.Value), StringComparer.Ordinal);
        var planned = new List<PlannedEquipmentRow>();
        // Resolve and count everything before the first insert, so a capacity failure cannot result in a partial plan.
        foreach (var (database, db) in loaded)
        {
            var table = NewUniformEquipmentPlan.EquipmentTable(db);
            var jerseyTable = db.Tables[Jerseys];
            foreach (var addition in wanted)
            {
                var jerseyRows = Enumerable.Range(0, jerseyTable.InUse).Where(row =>
                    db.GetInt(jerseyTable, row, "teamid") == addition.TeamId
                    && db.GetInt(jerseyTable, row, "style") == addition.Style
                    && db.GetInt(jerseyTable, row, "variant") == addition.TargetVariant).ToList();
                if (jerseyRows.Count != 1) throw new InvalidDataException(
                    $"{database}: {addition.Team} {addition.SourceKit} has {jerseyRows.Count} target jersey rows; plan the new uniform first.");
                long art = db.GetInt(jerseyTable, jerseyRows[0], "teamartid");
                int sourceVariant = SourceVariant(addition);
                foreach (int equipment in EquipmentTypes)
                {
                    var candidates = source27.Where(r => r.Team == addition.TeamId && r.Equipment == equipment
                        && (r.Variant == -1 || r.Variant == sourceVariant)).ToList();
                    var generic = SelectCurrent(candidates.Where(r => r.Brand == -1).ToList(), sourceVariant);
                    var selected = new List<EquipmentSourceCandidate>();
                    if (generic is null)
                    {
                        // NHL 27 does not give every numbered default kit an explicit row. Use its corresponding current-era
                        // home/away default. Florida has only default 1 for type 4; that sole current default serves both kits.
                        // A missing pair with multiple possible defaults is ambiguous and must fail, not pick a historical color.
                        int defaultVariant = addition.SourceKit == "away" ? 1 : 0;
                        var defaults = source27.Where(r => r.Team == addition.TeamId && r.Equipment == equipment
                            && r.Brand == -1 && r.Era == 2 && r.Variant is 0 or 1).ToList();
                        generic = defaults.Where(r => r.Variant == defaultVariant).OrderBy(r => r.SourceOrder).FirstOrDefault();
                        if (generic is null && defaults.Select(r => r.Variant).Distinct().Count() == 1)
                            generic = defaults.OrderBy(r => r.SourceOrder).FirstOrDefault();
                    }
                    if (generic is not null) selected.Add(generic);
                    // A brand-specific fallback is only carried when the target actually has that brand's donor.
                    // It supplements the generic row, not a duplicate of it. An older wildcard is acceptable here,
                    // but an old exact alternate is not: it can belong to a completely different historical uniform.
                    var brands = candidates.Where(r => r.Brand >= 0).Select(r => r.Brand).Distinct().Order().ToList();
                    foreach (long brand in brands)
                    {
                        bool targetHasBrand = Enumerable.Range(0, table.InUse).Any(row => db.GetInt(table, row, "team") == addition.TeamId
                            && db.GetInt(table, row, "brand") == brand && db.GetInt(table, row, "equipment") == equipment
                            && db.GetInt(table, row, "jerseyartid") == art && db.GetInt(table, row, "variant") <= 1);
                        if (!targetHasBrand) continue;
                        var branded = SelectBranded(candidates.Where(r => r.Brand == brand).ToList(), sourceVariant);
                        if (branded is not null) selected.Add(branded);
                    }
                    if (selected.Count == 0) throw new InvalidDataException(
                        $"{addition.Team} {addition.SourceKit}: no current NHL 27 equipment type {equipment} donor matches variant {sourceVariant}, its wildcard, or a documented current default.");
                    foreach (var chosen in selected)
                    {
                        var matchingCandidates = candidates.Where(r => r.Brand == chosen.Brand).ToList();
                        if (matchingCandidates.All(r => r.SourceOrder != chosen.SourceOrder)) matchingCandidates.Add(chosen);
                        matchingCandidates = matchingCandidates.OrderBy(r => r.SourceOrder).ToList();
                        var targetMatches = Enumerable.Range(0, table.InUse).Where(row =>
                            db.GetInt(table, row, "team") == addition.TeamId && db.GetInt(table, row, "brand") == chosen.Brand
                            && db.GetInt(table, row, "jerseyartid") == art && db.GetInt(table, row, "equipment") == equipment
                            && db.GetInt(table, row, "era") == chosen.Era && db.GetInt(table, row, "variant") == addition.TargetVariant).ToList();
                        if (targetMatches.Count > 1) throw new InvalidDataException(
                            $"{database}: ambiguous existing target equipment row for {addition.Team}, brand {chosen.Brand}, type {equipment}.");
                        var otherArt = Enumerable.Range(0, table.InUse).Where(row =>
                            db.GetInt(table, row, "team") == addition.TeamId && db.GetInt(table, row, "brand") == chosen.Brand
                            && db.GetInt(table, row, "equipment") == equipment && db.GetInt(table, row, "era") == chosen.Era
                            && db.GetInt(table, row, "variant") == addition.TargetVariant).ToList();
                        if (otherArt.Count > 0 && targetMatches.Count == 0) throw new InvalidDataException(
                            $"{database}: target equipment key collides with another jersey-art ID for {addition.Team} {addition.SourceKit}.");
                        planned.Add(new PlannedEquipmentRow(database, addition.TeamId, addition.Team, addition.SourceKit,
                            sourceVariant, addition.TargetVariant, chosen.Brand, art, equipment, chosen.Era, chosen.Zones,
                            targetMatches.Count == 1, chosen, matchingCandidates));
                    }
                }
            }
            int needed = planned.Count(r => r.Database == database && !r.AlreadyExists);
            if (table.InUse + needed > table.Slots) throw new InvalidDataException(
                $"{database}: equipment needs {needed} spare rows; only {table.Slots - table.InUse} remain.");
        }

        foreach (var (database, db) in loaded)
        {
            var table = NewUniformEquipmentPlan.EquipmentTable(db);
            foreach (var item in planned.Where(r => r.Database == database))
            {
                int row = Find(db, table, item);
                if (row < 0)
                {
                    int donor = FindDonor(db, table, item);
                    var block = Enumerable.Range(0, table.InUse).Where(r => db.GetInt(table, r, "team") == item.Team).ToList();
                    if (block.Count == 0) throw new InvalidDataException($"{database}: team {item.Team} has no equipment rows.");
                    int at = block.Last() + 1;
                    table = db.InsertRecord(table, at, donor);
                    db.SetInt(table, at, "team", item.Team);
                    db.SetInt(table, at, "brand", item.Brand);
                    db.SetInt(table, at, "jerseyartid", item.JerseyArtId);
                    db.SetInt(table, at, "equipment", item.Equipment);
                    db.SetInt(table, at, "era", item.Era);
                    db.SetInt(table, at, "variant", item.TargetVariant);
                    row = at;
                }
                foreach (string zone in NewUniformEquipmentPlan.Zones)
                    db.SetInt(table, row, zone, item.Zones[zone]);
            }
            for (int row = 0; row < table.InUse; row++) db.SetInt(table, row, "index", row);
        }

        var output = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var result = new NewUniformEquipmentPlan([], planned);
        foreach (var (database, db) in loaded)
        {
            byte[] saved = db.Save();
            if (saved.Length != sourceDatabases[database].Length) throw new InvalidDataException($"{database}: equipment changed the database length.");
            var check = T3Db.Load((byte[])saved.Clone(), metaXml);
            result.Verify(database, check);
            VerifyOriginals(database, sourceDatabases[database], saved, originals[database], metaXml);
            output.Add(database, saved);
        }
        // Rebuilding the same input must not allocate more rows or change the resulting bytes.
        return new NewUniformEquipmentPlan(output, planned);
    }

    public static int SourceVariant(NewUniformAddition addition)
    {
        string word = addition.SourceFolder27?.Trim().Replace('\\', '/').TrimEnd('/').Split('/')[^1] ?? "";
        int variant = Array.IndexOf(TextureName.Numbers, word);
        if (variant < 0 || variant > 25) throw new InvalidDataException($"{addition.Team}: unknown numbered NHL 27 variant '{word}'.");
        return variant;
    }

    static EquipmentSourceCandidate? SelectCurrent(List<EquipmentSourceCandidate> rows, int variant) =>
        rows.Where(r => r.Era == 2).OrderByDescending(r => r.Variant == variant).ThenBy(r => r.SourceOrder).FirstOrDefault();

    static EquipmentSourceCandidate? SelectBranded(List<EquipmentSourceCandidate> rows, int variant) =>
        SelectCurrent(rows, variant)
        ?? rows.Where(r => r.Era == 0).OrderByDescending(r => r.Variant == variant).ThenBy(r => r.SourceOrder).FirstOrDefault()
        ?? rows.Where(r => r.Era == -1 && r.Variant == -1).OrderBy(r => r.SourceOrder).FirstOrDefault();

    static EquipmentSourceCandidate Candidate(T3Csv table, string[] row, int order)
    {
        var zones = NewUniformEquipmentPlan.Zones.ToDictionary(z => z, z => (long)table.Int(row, z), StringComparer.Ordinal);
        return new EquipmentSourceCandidate(order, table.Int(row, "index"), table.Int(row, "team"), table.Int(row, "brand"),
            table.Int(row, "jerseyartid"), table.Int(row, "equipment"), table.Int(row, "era"), table.Int(row, "variant"), zones);
    }

    static int Find(T3Db db, T3Db.Table table, PlannedEquipmentRow item) =>
        Enumerable.Range(0, table.InUse).FirstOrDefault(row => NewUniformEquipmentPlan.KeyMatches(db, table, row, item), -1);

    static int FindDonor(T3Db db, T3Db.Table table, PlannedEquipmentRow item)
    {
        var candidates = Enumerable.Range(0, table.InUse).Where(row => db.GetInt(table, row, "team") == item.Team
            && db.GetInt(table, row, "brand") == item.Brand && db.GetInt(table, row, "jerseyartid") == item.JerseyArtId
            && db.GetInt(table, row, "equipment") == item.Equipment && db.GetInt(table, row, "variant") <= 1).ToList();
        if (candidates.Count == 0) throw new InvalidDataException(
            $"{item.TeamName}: no target brand/art equipment donor for brand {item.Brand}, type {item.Equipment}.");
        return candidates.OrderByDescending(row => db.GetInt(table, row, "era") == 2)
            .ThenByDescending(row => db.GetInt(table, row, "variant") == -1)
            .ThenByDescending(row => db.GetInt(table, row, "era") == 0).First();
    }

    sealed record OriginalRow(long Team, long Brand, long Art, long Equipment, long Era, long Variant, int Occurrence, byte[] Bytes);

    static List<OriginalRow> Snapshot(T3Db db)
    {
        var table = NewUniformEquipmentPlan.EquipmentTable(db);
        var result = new List<OriginalRow>();
        for (int row = 0; row < table.InUse; row++)
        {
            var copy = db.Bytes.AsSpan(table.RecordsAt + row * table.RecordBytes, table.RecordBytes).ToArray();
            result.Add(new OriginalRow(db.GetInt(table, row, "team"), db.GetInt(table, row, "brand"), db.GetInt(table, row, "jerseyartid"),
                db.GetInt(table, row, "equipment"), db.GetInt(table, row, "era"), db.GetInt(table, row, "variant"),
                result.Count(r => r.Team == db.GetInt(table, row, "team") && r.Brand == db.GetInt(table, row, "brand")
                    && r.Art == db.GetInt(table, row, "jerseyartid") && r.Equipment == db.GetInt(table, row, "equipment")
                    && r.Era == db.GetInt(table, row, "era") && r.Variant == db.GetInt(table, row, "variant")), copy));
        }
        return result;
    }

    static void VerifyOriginals(string database, byte[] before, byte[] after, List<OriginalRow> originals, string metaXml)
    {
        var a = T3Db.Load((byte[])before.Clone(), metaXml);
        var b = T3Db.Load((byte[])after.Clone(), metaXml);
        foreach (var other in b.Tables.Values.Where(t => t.Name != NewUniformEquipmentPlan.TableName))
            if (!before.AsSpan(other.Start, other.End - other.Start).SequenceEqual(after.AsSpan(other.Start, other.End - other.Start)))
                throw new InvalidDataException($"{database}: equipment planning changed unrelated table {other.Name}.");
        var at = NewUniformEquipmentPlan.EquipmentTable(a);
        var bt = NewUniformEquipmentPlan.EquipmentTable(b);
        if (originals.Count != at.InUse) throw new InvalidDataException($"{database}: the original equipment snapshot is incomplete.");
        int original = 0;
        for (int output = 0; output < bt.InUse; output++)
        {
            bool same = original < at.InUse && SameKey(a, at, original, b, bt, output);
            long variant = b.GetInt(bt, output, "variant");
            if (!same)
            {
                // A genuinely inserted reserved-variant row is skipped; the next output row must line up with the same
                // original again. Comparing before[row] to after[row] here would compare an original to its new neighbor.
                if (variant is not (24 or 25)) throw new InvalidDataException(
                    $"{database}: unexpected non-reserved equipment row at output row {output}; an original row was removed or reordered.");
                continue;
            }
            var oldBytes = a.Bytes.AsSpan(at.RecordsAt + original * at.RecordBytes, at.RecordBytes).ToArray();
            var newBytes = b.Bytes.AsSpan(bt.RecordsAt + output * bt.RecordBytes, bt.RecordBytes).ToArray();
            ClearIndex(at, oldBytes);
            ClearIndex(bt, newBytes);
            if (!oldBytes.AsSpan().SequenceEqual(newBytes) && variant is not (24 or 25)) throw new InvalidDataException(
                $"{database}: original stock equipment row {original} changed at output row {output}.");
            original++;
        }
        if (original != at.InUse) throw new InvalidDataException(
            $"{database}: only {original} of {at.InUse} original equipment rows remain after planning.");
    }

    static bool SameKey(T3Db a, T3Db.Table at, int ar, T3Db b, T3Db.Table bt, int br) =>
        a.GetInt(at, ar, "team") == b.GetInt(bt, br, "team")
        && a.GetInt(at, ar, "brand") == b.GetInt(bt, br, "brand")
        && a.GetInt(at, ar, "jerseyartid") == b.GetInt(bt, br, "jerseyartid")
        && a.GetInt(at, ar, "equipment") == b.GetInt(bt, br, "equipment")
        && a.GetInt(at, ar, "era") == b.GetInt(bt, br, "era")
        && a.GetInt(at, ar, "variant") == b.GetInt(bt, br, "variant");

    static void ClearIndex(T3Db.Table table, byte[] record)
    {
        var field = table.Fields["index"];
        for (int bit = 0; bit < field.Depth; bit++)
        {
            int at = field.BitOffset + bit;
            record[at >> 3] &= (byte)~(1 << (at & 7));
        }
    }
}
