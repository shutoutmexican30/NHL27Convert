// PortraitPrep <teams.tsv>
// Plans portrait slots for every player on a current NHL roster from the synced dumps in dbwork\<db>\after (nhl_game
// first), downloads the NHL API headshots and converts them to NHL 24's 512x340 layout in
// %LOCALAPPDATA%\CenterIceModdingSuite\portraits\game\p<artid>.png. Writes portraits\plan.json. Writes nothing to any game.
using System.IO.Compression;
using System.Text.Json;
using Suite.Core;
using Suite.Rosters;

var teamMap = RosterSync.LoadTeamMap(args[0]);
var dbs = new[] { "nhl_game", "initial_experience", "entry_draft", "expansion_draft", "threes_campaign" };
var phase = args.Contains("--before") ? "before" : "after";
var dumps = dbs.Select(d => Path.Combine(RosterDbJob.WorkDir, d, phase)).Where(Directory.Exists).ToList();
if (dumps.Count != dbs.Length) throw new InvalidOperationException($"Expected all {dbs.Length} database dumps in {phase}; found {dumps.Count}.");
var index = Path.Combine(SuitePaths.Local, "index", "nhl24", "assets.json.gz");
using var gz = new GZipStream(File.OpenRead(index), CompressionMode.Decompress);
using var doc = JsonDocument.Parse(gz);
var slots = doc.RootElement.GetProperty("Entries").EnumerateArray().Select(e => e.GetProperty("Path").GetString()!)
    .Where(p => p.StartsWith(Portraits.SlotFolder)).Select(p => p[Portraits.SlotFolder.Length..]).ToHashSet();
var day = DateOnly.FromDateTime(DateTime.Today);
var api = await new NhlApi().AllRostersAsync(day);
var include = Array.IndexOf(args, "--include-prospects");
if (include >= 0)
    foreach (var p in JsonSerializer.Deserialize<List<ApiPlayer>>(File.ReadAllText(args[include + 1])) ?? [])
    {
        if (!api.TryGetValue(p.TeamAbbrev, out var list)) api[p.TeamAbbrev] = list = [];
        if (!list.Any(x => x.Id == p.Id)) list.Add(p);
    }
var warnings = new List<string>();
var plan = Portraits.Plan(dumps, api, teamMap, slots, warnings);
Console.WriteLine($"{slots.Count} portrait slots in NHL 24; {plan.Count} players planned: {plan.Count(p => !p.NewSlot)} keep their slot, {plan.Count(p => p.NewSlot)} get a free one");
var built = await Portraits.PrepareAsync(plan, s =>
{
    var count = s.Split(' ').Last().Split('/')[0];
    if (int.TryParse(count, out var n) && (n == 1 || n % 50 == 0 || n == plan.Count)) Console.WriteLine(s);
});
Console.WriteLine($"built {built.Count(b => b.Png is not null)}, failed {built.Count(b => b.Png is null)}");
foreach (var b in built.Where(b => b.Png is null).Take(20)) Console.WriteLine("  failed: " + b.Name + " " + b.Headshot);
foreach (var w in warnings.Take(20)) Console.WriteLine("  warning: " + w);
