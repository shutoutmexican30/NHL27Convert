using Suite.Core;

namespace Suite.Rosters;

/// <summary>
/// The future draft-pick grid every roster database shares: game years 126–131 (drafts 2026–2031), 32 teams ×
/// 7 rounds a year. Ownership comes from data\draft_picks.tsv — NHL 27's grid updated with trades verified
/// against the public record (Wikipedia draft pages, NHL.com trade tracker). NHL 27's team order is NHL 24's;
/// the Utah Mammoth sits in slot 22, which is the Arizona Coyotes in NHL 24, so Utah's picks live there.
/// Every roster database's exhibitiondraftpick is rewritten to this one grid, so all five databases agree,
/// the same way the rosters do. leaguedraftpick (an active franchise's picks, kept in saves) is untouched.
/// </summary>
public sealed class DraftPicks
{
    public const string Table = "exhibitiondraftpick";
    public const int FirstYear = 126, Years = 6, Teams = 32, Rounds = 7;
    public const int ExpectedRows = Years * Teams * Rounds; // 1344, the slot count in every roster database

    readonly Dictionary<(int Year, int Round, int Orig), int> _owner;
    DraftPicks(Dictionary<(int, int, int), int> owner) => _owner = owner;

    public static string UserPath => Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "data")), "draft_picks.tsv");

    /// <summary>The traded-pick list: the user's copy first, then the seeded copy next to the suite. Null when neither exists.</summary>
    public static DraftPicks? Load()
    {
        var path = File.Exists(UserPath) ? UserPath : Path.Combine(AppContext.BaseDirectory, "data", "draft_picks.tsv");
        if (!File.Exists(path)) return null;
        var owner = new Dictionary<(int, int, int), int>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("year\t")) continue;
            var f = line.Split('\t');
            if (f.Length < 6) continue;
            if (!int.TryParse(f[0], out var year) || !int.TryParse(f[1], out var round)
                || !int.TryParse(f[4], out var orig) || !int.TryParse(f[5], out var ownerTeam)) continue;
            owner[(year - 1900, round, orig)] = ownerTeam;
        }
        return new DraftPicks(owner);
    }

    /// <summary>Who holds the pick: the traded owner, else the team it belongs to.</summary>
    public int Owner(int year, int round, int orig) => _owner.GetValueOrDefault((year, round, orig), orig);

    /// <summary>
    /// Rewrites the table to the shared grid. Keeps the slot count, the row order and every row in use, so the
    /// layout guard sees only record contents change. Years are normalised to 126–131 in every database, which
    /// also replaces the stale 122–128 ranges the stock databases carry.
    /// </summary>
    public void ApplyTo(string dumpDir, string outDir, WriteReport report)
    {
        if (!File.Exists(T3Csv.PathOf(dumpDir, Table))) return; // a database without the table is left alone
        var picks = T3Csv.Load(dumpDir, Table);
        int slots = picks.Rows.Count;
        var used = picks.Rows.Take(picks.ValidCount).ToList();
        if (used.Count != ExpectedRows)
            throw new InvalidOperationException($"{Table}: {used.Count} rows in use, expected {ExpectedRows}; the shared grid would not fit.");
        int changed = 0;
        for (int i = 0; i < used.Count; i++)
        {
            int year = FirstYear + i / (Teams * Rounds);
            int overall = i % (Teams * Rounds);
            int round = overall / Teams + 1;
            int orig = overall % Teams;
            var r = used[i];
            int owner = Owner(year, round, orig);
            if (picks.Int(r, "year") != year || picks.Int(r, "pickoverall") != overall || picks.Int(r, "round") != round
                || picks.Int(r, "origteamid") != orig || picks.Int(r, "owner") != owner) changed++;
            picks.Set(r, "year", year);
            picks.Set(r, "pickoverall", overall);
            picks.Set(r, "round", round);
            picks.Set(r, "origteamid", orig);
            picks.Set(r, "owner", owner);
        }
        picks.ReplaceUsed(used);
        if (picks.Rows.Count != slots) throw new InvalidOperationException($"{Table} changed its slot count; nothing written.");
        picks.Save(outDir);
        report.Tables.Add(Table);
        report.Counts["DraftPickRowsSet"] = changed;
    }
}
