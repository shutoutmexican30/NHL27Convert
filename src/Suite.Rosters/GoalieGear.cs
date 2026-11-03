namespace Suite.Rosters;

/// <summary>
/// The NHL 27 goalie-equipment table supplies the individual colour zones and the selected pads, gloves, stick and
/// mask.  NHL 24 accepts only its own equipment ids, so a source model is adopted only when that exact id is already
/// used by NHL 24; otherwise the player's NHL 24 model remains in place and receives the correct NHL 27 colours.
/// </summary>
public sealed class GoalieGear
{
    const string Table = "exhibitiongoalieequipment";
    const string TeamTable = "stockgoalieequipment";
    static readonly string[] PlayerModels = ["pads", "trapper", "blocker", "stick", "masktype"];
    static readonly string[] TeamModels = [.. PlayerModels, "maskid"];
    readonly Dictionary<string, Dictionary<string, string>> _byGameId = new(StringComparer.Ordinal);
    readonly Dictionary<(string Team, string League), Dictionary<string, string>> _byTeam = new();
    readonly Dictionary<string, HashSet<string>> _validModels = new(StringComparer.Ordinal);
    public int FromNhl27 { get; private set; }

    public static GoalieGear? Build(string? nhl27Dir, string mainDump, List<string> notes)
    {
        if (nhl27Dir is null || !File.Exists(T3Csv.PathOf(nhl27Dir, Table)) || !File.Exists(T3Csv.PathOf(nhl27Dir, TeamTable)))
        {
            notes.Add("No complete NHL 27 goalie-equipment dump; kept NHL 24 goalie masks and pad colours.");
            return null;
        }
        var target = T3Csv.Load(mainDump, Table);
        var source = T3Csv.Load(nhl27Dir, Table);
        var gear = new GoalieGear();
        var stock = T3Csv.Load(mainDump, TeamTable);
        foreach (var model in TeamModels.Where(target.Has))
            gear._validModels[model] = target.Rows.Take(target.ValidCount).Select(r => target.Get(r, model)).ToHashSet(StringComparer.Ordinal);
        foreach (var model in TeamModels.Where(stock.Has))
            if (gear._validModels.TryGetValue(model, out var allowed)) allowed.UnionWith(stock.Rows.Take(stock.ValidCount).Select(r => stock.Get(r, model)));
        foreach (var row in source.Rows.Take(source.ValidCount))
            gear._byGameId.TryAdd(source.Get(row, "game_id"), source.Header.ToDictionary(c => c, c => source.Get(row, c), StringComparer.Ordinal));
        var stock27 = T3Csv.Load(nhl27Dir, TeamTable);
        foreach (var row in stock27.Rows.Take(stock27.ValidCount))
            gear._byTeam.TryAdd((stock27.Get(row, "team"), stock27.Get(row, "league")), stock27.Header.ToDictionary(c => c, c => stock27.Get(row, c), StringComparer.Ordinal));
        notes.Add($"Goalie gear: {gear._byGameId.Count} NHL 27 player rows and {gear._byTeam.Count} team-mask rows available; all colour zones and NHL 24-compatible models will be synchronized.");
        return gear;
    }

    /// <summary>Applies safe model ids plus all shared colour-zone fields to one database.</summary>
    public int ApplyTo(string dumpDir, string outDir, WriteReport report)
    {
        if (!File.Exists(T3Csv.PathOf(dumpDir, Table))) return 0;
        var table = T3Csv.Load(dumpDir, Table);
        int changed = 0, matched = 0, fallbackModels = 0;
        foreach (var row in table.Rows.Take(table.ValidCount))
        {
            if (!_byGameId.TryGetValue(table.Get(row, "game_id"), out var source)) continue;
            matched++;
            bool any = false;
            foreach (var col in table.Header)
            {
                if (!source.TryGetValue(col, out var value) || col == "game_id") continue;
                bool colour = col.Contains("zone", StringComparison.Ordinal) || col == "sticktapecolor";
                // Never adopt the NHL 27 pad/trapper/blocker/stick model: the model carries the brand wordmark, so
                // copying it renames the gear on the player. Only the mask type travels, and the team mask comes from the
                // team table below.
                bool model = col == "masktype";
                if (!colour && !model && col is not "showcustomcolors" and not "showcustomstick") continue;
                if (model && _validModels.TryGetValue(col, out var allowed) && !allowed.Contains(value)) { fallbackModels++; continue; }
                if (table.Get(row, col) != value) { table.Set(row, col, value); any = true; }
            }
            if (any) changed++;
        }
        FromNhl27 += matched;
        if (changed > 0) { table.Save(outDir); report.Tables.Add(Table); }

        // Masks are team artwork.  Use the NHL 27 row matching NHL 24's (team, league), but only point at a model
        // ID that NHL 24 already uses.  This keeps a team mask tied to its team id rather than to a goalie/player id.
        if (!File.Exists(T3Csv.PathOf(dumpDir, TeamTable)))
        {
            report.Counts["TeamGoalieGearRowsChanged"] = 0;
            report.Counts["GoalieGearModelFallbacks"] = fallbackModels;
            return changed;
        }
        var stock = T3Csv.Load(dumpDir, TeamTable);
        int stockChanged = 0;
        foreach (var row in stock.Rows.Take(stock.ValidCount))
        {
            if (!_byTeam.TryGetValue((stock.Get(row, "team"), stock.Get(row, "league")), out var source)) continue;
            bool any = false;
            foreach (var col in stock.Header)
            {
                if (!source.TryGetValue(col, out var value) || col is "game_id" or "team" or "league" or "key") continue;
                bool colour = col.Contains("zone", StringComparison.Ordinal) || col == "sticktapecolor";
                bool model = TeamModels.Contains(col, StringComparer.Ordinal);
                if (!colour && !model && col is not "showcustomcolors" and not "showcustomstick") continue;
                if (model && _validModels.TryGetValue(col, out var allowed) && !allowed.Contains(value)) { fallbackModels++; continue; }
                if (stock.Get(row, col) != value) { stock.Set(row, col, value); any = true; }
            }
            if (any) stockChanged++;
        }
        if (stockChanged > 0) { stock.Save(outDir); report.Tables.Add(TeamTable); }
        report.Counts["GoalieGearRowsChanged"] = changed;
        report.Counts["TeamGoalieGearRowsChanged"] = stockChanged;
        report.Counts["GoalieGearModelFallbacks"] = fallbackModels;
        return changed;
    }
}
