namespace Suite.Rosters;

/// <summary>One new selectable uniform row. Existing home/away/alternate rows are never repurposed.</summary>
public sealed record NewJerseyRowRequest(int Team, int Variant, bool IsLight, int Style = 2);

public sealed record PlannedJerseyRow(
    string Database, int Team, int Style, int Variant, long JerseyIndex, long OrderId,
    long TemplateVariant, int InsertAt, bool IsLight, bool AlreadyExists);

/// <summary>A fully validated, coordinated database edit. Building it never writes to disk or a game.</summary>
public sealed class JerseyRowPlan
{
    public IReadOnlyDictionary<string, byte[]> Databases { get; }
    public IReadOnlyList<PlannedJerseyRow> Rows { get; }
    public IReadOnlyList<string> ChangedDatabases { get; }
    public int InsertedRows => Rows.Count(r => !r.AlreadyExists);

    internal JerseyRowPlan(Dictionary<string, byte[]> databases, List<PlannedJerseyRow> rows)
    {
        Databases = databases;
        Rows = rows;
        ChangedDatabases = rows.Where(r => !r.AlreadyExists).Select(r => r.Database).Distinct(StringComparer.Ordinal).ToArray();
    }
}

public static class JerseyRowPlanner
{
    public const string TableName = "stockteamjerseys";
    public sealed record Key(int Team, int Style, int Variant, int Occurrence = 0);

    /// <summary>
    /// Plans the same new rows in every supplied database, using only spare table capacity. Every original logical row
    /// is compared by team/style/variant after insertion; unrelated tables must remain byte-identical. Existing requested
    /// keys are idempotent no-ops, while duplicate keys inside a database or request set are hard failures.
    /// </summary>
    public static JerseyRowPlan Build(
        IReadOnlyDictionary<string, byte[]> sourceDatabases,
        string metaXml,
        IEnumerable<NewJerseyRowRequest> requests)
    {
        var wanted = requests.ToList();
        if (wanted.Count == 0) throw new InvalidDataException("No new uniform rows were requested.");
        var requestKeys = new HashSet<Key>();
        foreach (var request in wanted)
        {
            if (request.Team < 0) throw new InvalidDataException("Team ID cannot be negative.");
            if (request.Variant is < 2 or > 25) throw new InvalidDataException(
                $"Team {request.Team}: new-uniform variant {request.Variant} is outside the reserved alternate range 2–25.");
            if (!requestKeys.Add(new Key(request.Team, request.Style, request.Variant)))
                throw new InvalidDataException($"Duplicate requested jersey row: team {request.Team}, style {request.Style}, variant {request.Variant}.");
        }
        if (sourceDatabases.Count == 0) throw new InvalidDataException("No databases were supplied.");

        var loaded = sourceDatabases.ToDictionary(kv => kv.Key,
            kv => T3Db.Load((byte[])kv.Value.Clone(), metaXml), StringComparer.Ordinal);
        var originalRows = new Dictionary<string, Dictionary<Key, byte[]>>(StringComparer.Ordinal);
        foreach (var (name, db) in loaded)
        {
            if (!db.Tables.TryGetValue(TableName, out var table))
                throw new InvalidDataException($"{name}: {TableName} is missing; the coordinated apply was not planned.");
            originalRows[name] = RowsByKey(db, table);
            foreach (var request in wanted)
                if (originalRows[name].Keys.Count(k => k.Team == request.Team && k.Style == request.Style
                        && k.Variant == request.Variant) > 1)
                    throw new InvalidDataException($"{name}: duplicate existing target row: team {request.Team}, style {request.Style}, variant {request.Variant}.");
            int needed = wanted.Count(w => !originalRows[name].ContainsKey(new Key(w.Team, w.Style, w.Variant)));
            if (table.InUse + needed > table.Slots)
                throw new InvalidDataException($"{name}: {TableName} needs {needed} spare slots but only {table.Slots - table.InUse} remain.");
        }

        var planned = new List<PlannedJerseyRow>();
        foreach (var (name, db) in loaded)
        {
            var table = db.Tables[TableName];
            foreach (var request in wanted)
            {
                var key = new Key(request.Team, request.Style, request.Variant);
                var block = Enumerable.Range(0, table.InUse).Where(r => db.GetInt(table, r, "teamid") == request.Team).ToList();
                if (block.Count == 0 || block.Last() - block.First() + 1 != block.Count)
                    throw new InvalidDataException($"{name}: team {request.Team}'s jersey rows are missing or not one contiguous block.");
                int existing = Find(db, table, key);
                if (existing >= 0)
                {
                    planned.Add(new PlannedJerseyRow(name, request.Team, request.Style, request.Variant,
                        db.GetInt(table, existing, "jerseyindex"), db.GetInt(table, existing, "orderid"),
                        db.GetInt(table, existing, "variant"), existing, request.IsLight, true));
                    continue;
                }
                var styled = block.Where(r => db.GetInt(table, r, "style") == request.Style).ToList();
                int template = styled.Where(r => db.GetInt(table, r, "variant") == 2)
                    .DefaultIfEmpty(styled.LastOrDefault(r => db.GetInt(table, r, "isdefault") == 0, -1)).First();
                if (template < 0) throw new InvalidDataException(
                    $"{name}: team {request.Team} has no non-default style {request.Style} row to use as the row template.");
                long templateVariant = db.GetInt(table, template, "variant");
                long jerseyIndex = block.Max(r => db.GetInt(table, r, "jerseyindex")) + 1;
                long orderId = styled.Max(r => db.GetInt(table, r, "orderid")) + 1;
                int at = block.Last() + 1;
                table = db.InsertRecord(table, at, template);
                db.SetInt(table, at, "variant", request.Variant);
                db.SetInt(table, at, "fevariant", request.Variant);
                db.SetInt(table, at, "variantleadingscorer", request.Variant);
                db.SetInt(table, at, "jerseyindex", jerseyIndex);
                db.SetInt(table, at, "orderid", orderId);
                db.SetInt(table, at, "isdefault", 0);
                db.SetInt(table, at, "active", 1);
                db.SetInt(table, at, "islightcolor", request.IsLight ? 1 : 0);
                planned.Add(new PlannedJerseyRow(name, request.Team, request.Style, request.Variant,
                    jerseyIndex, orderId, templateVariant, at, request.IsLight, false));
            }
        }

        var output = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, db) in loaded)
        {
            byte[] saved = db.Save();
            if (saved.Length != sourceDatabases[name].Length) throw new InvalidDataException($"{name}: database file length changed.");
            var check = T3Db.Load((byte[])saved.Clone(), metaXml);
            var table = check.Tables[TableName];
            var afterRows = RowsByKey(check, table);
            foreach (var (key, before) in originalRows[name])
            {
                if (!afterRows.TryGetValue(key, out var after) || !before.AsSpan().SequenceEqual(after))
                    throw new InvalidDataException($"{name}: original jersey row changed: team {key.Team}, style {key.Style}, variant {key.Variant}.");
            }
            foreach (var other in check.Tables.Values.Where(t => t.Name != TableName))
                if (!sourceDatabases[name].AsSpan(other.Start, other.End - other.Start)
                        .SequenceEqual(saved.AsSpan(other.Start, other.End - other.Start)))
                    throw new InvalidDataException($"{name}: unrelated table changed: {other.Name}.");
            foreach (var row in planned.Where(r => r.Database == name && !r.AlreadyExists))
            {
                int at = Find(check, table, new Key(row.Team, row.Style, row.Variant));
                if (at < 0 || check.GetInt(table, at, "jerseyindex") != row.JerseyIndex
                    || check.GetInt(table, at, "orderid") != row.OrderId
                    || check.GetInt(table, at, "fevariant") != row.Variant
                    || check.GetInt(table, at, "variantleadingscorer") != row.Variant
                    || check.GetInt(table, at, "isdefault") != 0
                    || check.GetInt(table, at, "active") != 1
                    || check.GetInt(table, at, "islightcolor") != (row.IsLight ? 1 : 0))
                    throw new InvalidDataException($"{name}: new jersey row did not read back for team {row.Team}, variant {row.Variant}.");
            }
            output.Add(name, saved);
        }
        return new JerseyRowPlan(output, planned);
    }

    static Dictionary<Key, byte[]> RowsByKey(T3Db db, T3Db.Table table)
    {
        var result = new Dictionary<Key, byte[]>();
        for (int r = 0; r < table.InUse; r++)
        {
            int team = (int)db.GetInt(table, r, "teamid"), style = (int)db.GetInt(table, r, "style"),
                variant = (int)db.GetInt(table, r, "variant"), occurrence = 0;
            while (result.ContainsKey(new Key(team, style, variant, occurrence))) occurrence++;
            result.Add(new Key(team, style, variant, occurrence),
                db.Bytes.AsSpan(table.RecordsAt + r * table.RecordBytes, table.RecordBytes).ToArray());
        }
        return result;
    }

    static int Find(T3Db db, T3Db.Table table, Key key)
    {
        for (int r = 0; r < table.InUse; r++)
            if (db.GetInt(table, r, "teamid") == key.Team && db.GetInt(table, r, "style") == key.Style
                && db.GetInt(table, r, "variant") == key.Variant) return r;
        return -1;
    }
}
