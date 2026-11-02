using System.Text;
using Suite.Core;

namespace Suite.Rosters;

/// <summary>One player row of a roster database (exhibitionplayerbiotable), as read for the sync.</summary>
public sealed record DbPlayer(int Row, string GameId, string First, string Last, DateOnly? Birth, int Team, int Jersey, int Position, bool HasPortrait, int HeadId);

/// <summary>A roster database as the sync sees it (read-only snapshot).</summary>
public sealed record RosterDb(string Name, string Source, List<DbPlayer> Players);

public enum ChangeKind { Move, Number, Position, Release, NotInDb, Same }

public sealed record RosterChange(ChangeKind Kind, string Player, string? From, string? To, long? ApiId, int? Row);

/// <summary>
/// Plans a roster sync against every roster database. Each database is planned on its own terms (its own rows), from
/// the same NHL API result, so all of them end up with the same team, jersey number and position for every rostered player.
/// Read-only: produces per-database change lists; writing comes later and goes through the layout guard.
/// </summary>
public static class RosterSync
{
    /// <summary>NHL 24 bio position codes: 0 C, 1 LW, 2 RW, 3 D, 4 G.</summary>
    public static int PositionCode(string api) => api switch { "C" => 0, "L" => 1, "R" => 2, "D" => 3, "G" => 4, _ => -1 };
    public static string PositionName(int code) => code switch { 0 => "C", 1 => "LW", 2 => "RW", 3 => "D", 4 => "G", _ => "?" };

    /// <summary>Bio dates: year since 1900, 0-based month and day.</summary>
    public static DateOnly? BioDate(int year, int month, int day)
    {
        try { return new DateOnly(1900 + year, month + 1, day + 1); } catch { return null; }
    }

    /// <summary>API team abbreviation → NHL 24 team index, from data\nhlapi_teams.tsv (hand-editable).</summary>
    public static Dictionary<string, int> LoadTeamMap(string path)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length >= 2 && int.TryParse(f[1], out var idx)) map[f[0].Trim()] = idx;
        }
        return map;
    }

    public static string Norm(string s)
    {
        // Scandinavian ø has no FormD decomposition. The NHL API spells Røndbjerg as Rondbjerg.
        var d = s.Replace('ø', 'o').Replace('Ø', 'O').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark && char.IsLetter(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Matches an API player to a database row: birth date + last name, then birth date + first name, then full name
    /// (only when unique). Returns null when nothing is certain.
    /// </summary>
    public static DbPlayer? Match(ApiPlayer p, ILookup<string, DbPlayer> byLast, ILookup<string, DbPlayer> byFull,
        IReadOnlySet<string>? preferIds = null)
    {
        var last = byLast[Norm(p.Last)].ToList();
        if (p.Birth is { } b)
        {
            var hit = last.Where(x => x.Birth == b).ToList();
            if (hit.Count == 1) return hit[0];
            // An older snapshot can hold the same person twice ("Tomy" and "Thomas" Novak): take the main database's.
            if (hit.Count > 1 && preferIds is not null && hit.Where(x => preferIds.Contains(x.GameId)).ToList() is [var preferred]) return preferred;
            var full = byFull[Norm(p.First + p.Last)].Where(x => x.Birth == b).ToList();
            if (full.Count == 1) return full[0];
        }
        var byName = byFull[Norm(p.First + p.Last)].ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    public sealed record Plan(string Db, List<RosterChange> Changes)
    {
        public int Count(ChangeKind k) => Changes.Count(c => c.Kind == k);
    }

    /// <summary>The changes one database needs so its NHL rosters equal the API's.</summary>
    public static Plan PlanFor(RosterDb db, Dictionary<string, List<ApiPlayer>> api, Dictionary<string, int> teamMap)
    {
        var byLast = db.Players.ToLookup(x => Norm(x.Last));
        var byFull = db.Players.ToLookup(x => Norm(x.First + x.Last));
        var changes = new List<RosterChange>();
        var rostered = new HashSet<int>();
        var nhlTeams = teamMap.Values.ToHashSet();
        foreach (var (abbrev, players) in api)
        {
            if (!teamMap.TryGetValue(abbrev, out var team)) continue;
            foreach (var p in players)
            {
                var name = $"{p.First} {p.Last}";
                var m = Match(p, byLast, byFull);
                if (m is null) { changes.Add(new(ChangeKind.NotInDb, name, null, abbrev, p.Id, null)); continue; }
                rostered.Add(m.Row);
                bool any = false;
                if (m.Team != team) { changes.Add(new(ChangeKind.Move, name, m.Team.ToString(), team.ToString(), p.Id, m.Row)); any = true; }
                if (p.Number is { } n && m.Jersey != n) { changes.Add(new(ChangeKind.Number, name, m.Jersey.ToString(), n.ToString(), p.Id, m.Row)); any = true; }
                var pos = PositionCode(p.Position);
                if (pos >= 0 && m.Position != pos) { changes.Add(new(ChangeKind.Position, name, PositionName(m.Position), PositionName(pos), p.Id, m.Row)); any = true; }
                if (!any) changes.Add(new(ChangeKind.Same, name, null, null, p.Id, m.Row));
            }
        }
        // Players on an NHL team in this db but on no NHL roster today go to free agency.
        foreach (var x in db.Players.Where(x => nhlTeams.Contains(x.Team) && !rostered.Contains(x.Row)))
            changes.Add(new(ChangeKind.Release, $"{x.First} {x.Last}", x.Team.ToString(), "-1", null, x.Row));
        return new Plan(db.Name, changes);
    }
}
