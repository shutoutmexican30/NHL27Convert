// PortraitFix --target <game copy> [--apply]
// portraits\plan.json was planned against the roster dumps of its day; later roster writes moved artids, so its slots
// no longer match the game.  This re-plans every portrait against the game's own five databases (bio artid +
// hasportrait, T3Db, in place): a player keeps his artid when that slot exists and no other player in any database uses
// it, otherwise he gets a slot no database references, set in every database that has him.  hasportrait = 1 for all.
// Writes the re-planned portraits\plan.json (the old one kept as plan.before-fix.json) and the five databases to
// portraits\dbs\ for tools\PortraitWrite, which writes the pictures.  Without --apply nothing reaches the game.
using System.Text.Json;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
string Arg(string name) => args[Array.IndexOf(args, name) + 1];
var settings = SuiteSettings.Load();
var target = Path.GetFullPath(Arg("--target")).TrimEnd(Path.DirectorySeparatorChar);
foreach (var original in new[] { settings.Nhl24GameDir, settings.Nhl24BaseDir, settings.Nhl24UpdateDir, settings.Nhl24ReadView })
    if (!string.IsNullOrWhiteSpace(original) && string.Equals(Path.GetFullPath(original).TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase))
        { Say("REFUSED: the original game."); return 1; }
var meta = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game-meta.xml");
var names = RosterSyncRun.Databases.ToDictionary(d => d, RosterSyncRun.LegacyName);
var raw = names.ToDictionary(kv => kv.Key, kv => new LegacyFiles(target, settings).Read(kv.Value));
var dbs = raw.ToDictionary(kv => kv.Key, kv => T3Db.Load((byte[])kv.Value.Clone(), meta));

// Portrait slots this game ships (p<artid>).
var index = BundleIndex.Build(target);
var slots = index.ResByName.Keys.Where(k => k.StartsWith(Portraits.SlotFolder)).Select(k => k[(Portraits.SlotFolder.Length + 1)..])
    .Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n > 0).ToHashSet();
// Who uses which artid, across every database.
var users = new Dictionary<long, HashSet<long>>();
foreach (var db in dbs.Values)
{
    var bio = db.Tables["exhibitionplayerbiotable"];
    for (int r = 0; r < bio.InUse; r++)
    {
        long art = db.GetInt(bio, r, "artid");
        if (!users.TryGetValue(art, out var set)) users[art] = set = [];
        set.Add(db.GetInt(bio, r, "game_id"));
    }
}
var free = new Queue<long>(slots.Where(s => !users.ContainsKey(s)).Order().Select(s => (long)s));
Say($"{slots.Count} portrait slots in the game, {free.Count} unused by any database");

var planFile = Path.Combine(SuitePaths.Local, "portraits", "plan.json");
var plan = JsonSerializer.Deserialize<List<PortraitAssignment>>(File.ReadAllText(planFile))!;
var mainBio = dbs["nhl_game"].Tables["exhibitionplayerbiotable"];
var artOf = new Dictionary<long, long>();
for (int r = 0; r < mainBio.InUse; r++) artOf.TryAdd(dbs["nhl_game"].GetInt(mainBio, r, "game_id"), dbs["nhl_game"].GetInt(mainBio, r, "artid"));
var fixedPlan = new List<PortraitAssignment>();
int kept = 0, moved = 0, missing = 0, changedSlot = 0;
var assign = new Dictionary<long, long>();
foreach (var p in plan)
{
    long gid = long.Parse(p.GameId);
    if (p.Png is null || !File.Exists(p.Png) || !artOf.TryGetValue(gid, out var art)) { missing++; continue; }
    bool own = art > 0 && slots.Contains((int)art) && users.GetValueOrDefault(art, []).All(g => g == gid) && !assign.ContainsValue(art);
    if (!own)
    {
        if (free.Count == 0) { Say($"no free slot left for {p.Name}"); missing++; continue; }
        art = free.Dequeue();
        users[art] = [gid];
        moved++;
    }
    else kept++;
    if (art != p.ArtId) changedSlot++;
    assign[gid] = art;
    fixedPlan.Add(p with { ArtId = (int)art, NewSlot = !own });
}
// artid + hasportrait in every database that has the player.
var cells = new Dictionary<string, int>();
foreach (var (name, db) in dbs)
{
    var bio = db.Tables["exhibitionplayerbiotable"];
    int n = 0;
    for (int r = 0; r < bio.InUse; r++)
    {
        if (!assign.TryGetValue(db.GetInt(bio, r, "game_id"), out var art)) continue;
        if (db.GetInt(bio, r, "artid") != art) { db.SetInt(bio, r, "artid", art); n++; }
        if (db.GetInt(bio, r, "hasportrait") != 1) { db.SetInt(bio, r, "hasportrait", 1); n++; }
    }
    cells[name] = n;
}
Say($"{plan.Count} planned portraits: {kept} keep their own slot, {moved} get a free slot, {missing} skipped (no picture or not in the roster); " +
    $"{changedSlot} differ from the old plan. Database cells: {string.Join(", ", cells.Select(kv => $"{kv.Key} {kv.Value}"))}");

var output = new Dictionary<string, byte[]>();
var dbDir = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "portraits", "dbs"));
foreach (var (name, db) in dbs)
{
    var saved = cells[name] > 0 ? db.Save() : raw[name];
    if (saved.Length != raw[name].Length) throw new InvalidOperationException($"{name}: size changed");
    T3Db.Load((byte[])saved.Clone(), meta);   // every CRC valid again
    if (cells[name] > 0) output[names[name]] = saved;
    File.WriteAllBytes(Path.Combine(dbDir, name + ".db"), saved);
}
if (!WriteGate.Allowed(args)) { Say("dry run (pass --apply with --target to write the databases and the new plan)"); return 0; }

if (ApplyTarget.RunningGame() is { } running) { Say("REFUSED: " + running + " is running"); return 1; }
if (output.Count > 0)
{
    var backup = ApplyTarget.Backup(target);
    Say("backup: " + backup);
    foreach (var line in new LegacyFiles(target, settings).Replace(output)) SuiteLog.Write("portraitfix: " + line);
    var check = new LegacyFiles(target, settings);
    var wrong = output.Where(kv => !check.Read(kv.Key).AsSpan().SequenceEqual(kv.Value)).Select(kv => kv.Key).ToList();
    Say($"databases read back {(wrong.Count == 0 ? "identical" : "DIFFERENT: " + string.Join(", ", wrong))}");
    if (wrong.Count > 0) { ApplyTarget.RestoreFromBackup(target, backup); Say("FAILED; restored"); return 1; }
}
File.Copy(planFile, Path.Combine(Path.GetDirectoryName(planFile)!, "plan.before-fix.json"), true);
File.WriteAllText(planFile, JsonSerializer.Serialize(fixedPlan, new JsonSerializerOptions { WriteIndented = true }));
Say($"plan.json re-planned ({fixedPlan.Count} portraits). Next: PortraitWrite {dbDir}");
return 0;
