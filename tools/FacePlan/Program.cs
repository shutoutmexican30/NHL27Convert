// FacePlan <output-directory>: read-only inventory of NHL players with generic NHL 24 heads.
// Named textures are candidates, not proof that meshes, rigs, materials or ID bindings can be ported.
using System.Text;
using System.Text.Json;
using Suite.Core;
using Suite.Rosters;

if (args.Length != 1) { Console.WriteLine("usage: FacePlan <output-directory>"); return 2; }
var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var index24 = AssetIndex.TryLoad("nhl24") ?? throw new FileNotFoundException("NHL 24 index missing");
var index27 = AssetIndex.TryLoad("nhl27") ?? throw new FileNotFoundException("NHL 27 index missing");
const string licensed = "content/character/skater/shared/head/licensed/";
Dictionary<string, List<string>> Names(AssetIndex index) => index.Entries
    .Where(e => e.Path.StartsWith(licensed))
    .Select(e => e.Path[licensed.Length..].Split('/')[0]).Distinct()
    .GroupBy(RosterSync.Norm).ToDictionary(g => g.Key, g => g.ToList());
var names24 = Names(index24); var names27 = Names(index27);
var mainDir = Path.Combine(RosterDbJob.WorkDir, "nhl_game", "after");
var main = T3Csv.Load(mainDir, "exhibitionplayerbiotable");
var source = T3Csv.Load(Ratings.Nhl27Dir, "exhibitionplayerbiotable");
var srcPlayers = source.Rows.Take(source.ValidCount).Select((r, i) => new DbPlayer(i, source.Get(r, "game_id"), source.Get(r, "firstname"), source.Get(r, "lastname"),
    RosterSync.BioDate(source.Int(r, "year"), source.Int(r, "month"), source.Int(r, "day")), source.Int(r, "team"), source.Int(r, "jersey"), source.Int(r, "position"), false, source.Int(r, "headid"))).ToList();
var byLast = srcPlayers.ToLookup(p => RosterSync.Norm(p.Last));
var byFull = srcPlayers.ToLookup(p => RosterSync.Norm(p.First + p.Last));
var sourceUsers = srcPlayers.GroupBy(p => p.HeadId).ToDictionary(g => g.Key, g => g.Select(p => p.GameId).Distinct().Count());
var usedHeads = new Dictionary<int, HashSet<string>>();
foreach (var db in RosterSyncRun.Databases)
{
    var dump = Path.Combine(RosterDbJob.WorkDir, db, "after");
    foreach (var file in Directory.GetFiles(dump, "*.csv"))
    {
        var table = T3Csv.Load(dump, Path.GetFileNameWithoutExtension(file));
        if (!table.Has("headid")) continue;
        foreach (var row in table.Rows.Take(table.ValidCount))
            foreach (var col in new[] { "headid", "headtexture" }.Where(table.Has))
            {
                var id = table.Int(row, col);
                if (!usedHeads.TryGetValue(id, out var users)) usedHeads[id] = users = [];
                users.Add(table.Has("game_id") ? table.Get(row, "game_id") : db + ":" + table.Name + ":" + string.Join(',', row));
            }
    }
}
const string genericPrefix = "content/character/skater/shared/head/generic/male_";
var genericAssets = index24.Entries.Select(e => e.Path).Where(p => p.StartsWith(genericPrefix))
    .Select(p => p[genericPrefix.Length..].Split('/')[0]).Select(s => int.TryParse(s, out var id) ? id : -1).Where(id => id >= 60000).Distinct().Order().ToList();
var freeHeads = genericAssets.Where(id => !usedHeads.ContainsKey(id)).ToList();
var plan = new List<FaceCandidate>();
foreach (var row in main.Rows.Take(main.ValidCount).Where(r => main.Int(r, "team") is >= 0 and < 32 && main.Int(r, "headid") >= 60000))
{
    var first = main.Get(row, "firstname"); var last = main.Get(row, "lastname");
    var key = RosterSync.Norm(first + last);
    var probe = new ApiPlayer(0, first, last, null, "", RosterSync.BioDate(main.Int(row, "year"), main.Int(row, "month"), main.Int(row, "day")), null, "");
    var match = RosterSync.Match(probe, byLast, byFull);
    var sourceKey = match is null ? key : RosterSync.Norm(match.First + match.Last);
    var folders24 = names24.GetValueOrDefault(key) ?? names24.GetValueOrDefault(sourceKey) ?? [];
    var folders27 = names27.GetValueOrDefault(sourceKey) ?? names27.GetValueOrDefault(key) ?? [];
    if (folders27.Count == 0 && match is { HeadId: < 60000 })
    {
        var lastKey = RosterSync.Norm(match.Last);
        var bySurname = names27.Values.SelectMany(v => v).Where(f => RosterSync.Norm(f).EndsWith(lastKey, StringComparison.Ordinal)).Distinct().ToList();
        if (bySurname.Count == 1) folders27 = bySurname; // e.g. Matt Coronato -> matthew_coronato
    }
    // A named folder can belong to a namesake (e.g. the two Elias Petterssons). The matched source bio's generic
    // head binding takes precedence over a tempting name-only asset match.
    string status = match is null ? "no-NHL27-match" : match.HeadId >= 60000 ? "NHL27-also-uses-generic-head"
        : folders24.Count > 0 ? "named-assets-already-in-24" : folders27.Count > 0 ? "named-face-port-candidate" : "licensed-ID-needs-folder-resolution";
    int head = main.Int(row, "headid");
    plan.Add(new FaceCandidate(first + " " + last, main.Get(row, "game_id"), main.Int(row, "team"), head,
        match?.HeadId, match is null ? 0 : sourceUsers.GetValueOrDefault(match.HeadId), folders24, folders27,
        usedHeads.GetValueOrDefault(head)?.Count ?? 0, status));
}
plan = plan.OrderBy(p => p.Status).ThenBy(p => p.Name).ToList();
var report = new { GenericNhlPlayers = plan.Count, Counts = plan.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count()),
    GenericAssetSlots = genericAssets.Count, UnreferencedSlots = freeHeads,
    Caution = "Unreferenced means no headid/headtexture reference in the five dumped databases, including custom bio tables. Editor presets, saved careers, EBX bindings and runtime uses are not audited. No slot is approved for replacement.",
    Players = plan };
File.WriteAllText(Path.Combine(output, "face-plan.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
var csv = new StringBuilder("Name,GameId,TeamIndex,CurrentHead,Nhl27Head,Nhl27HeadUsers,CurrentHeadUsers,Status,Nhl24Folders,Nhl27Folders\r\n");
foreach (var p in plan)
    csv.AppendLine(string.Join(',', new[] { p.Name, p.GameId, p.TeamIndex.ToString(), p.CurrentHead.ToString(), p.Nhl27Head?.ToString() ?? "", p.Nhl27HeadUsers.ToString(), p.CurrentHeadUsers.ToString(), p.Status, string.Join('|', p.Nhl24Folders), string.Join('|', p.Nhl27Folders) }.Select(Csv)));
File.WriteAllText(Path.Combine(output, "face-plan.csv"), csv.ToString(), new UTF8Encoding(true));
Console.WriteLine($"{plan.Count} NHL players have generic NHL 24 heads; {genericAssets.Count} generic slots, {freeHeads.Count} unreferenced in the dumped databases.");
foreach (var group in plan.GroupBy(p => p.Status)) Console.WriteLine($"{group.Key}: {group.Count()}");
foreach (var p in plan.Where(p => p.Name.Contains("Yurov") || p.Name.Contains("Celebrini") || p.Name.Contains("Demidov"))) Console.WriteLine(JsonSerializer.Serialize(p));
return 0;

record FaceCandidate(string Name, string GameId, int TeamIndex, int CurrentHead, int? Nhl27Head, int Nhl27HeadUsers,
    List<string> Nhl24Folders, List<string> Nhl27Folders, int CurrentHeadUsers, string Status);
