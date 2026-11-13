using Suite.Core;
using Suite.Rosters;

// Contracts: NHL 27 prefill report and temp-copy test, or a write to a game copy.
//   Contracts --dbs <folder with the 5 .db files> [--nhl27 <exhibitionplayerbiotable.csv>] [--contracts <contracts.json|.csv>]
//             [--season 2023] [--out <folder>]        plan; with --out writes the changed databases there (never a game)
//   Contracts --target <CIMS game copy> [...] --apply  writes the game copy (backup, read-back, restore on failure)
string? Arg(string n) { int i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var settings = SuiteSettings.Load();
var map = new ContractMapping(GameSeason: int.Parse(Arg("--season") ?? "2023"));

Dictionary<string, byte[]> dbs;
if (Arg("--dbs") is { } dir)
    dbs = RosterSyncRun.Databases.Where(d => File.Exists(Path.Combine(dir, d + ".db"))).ToDictionary(d => d, d => File.ReadAllBytes(Path.Combine(dir, d + ".db")));
else if (Arg("--target") is { } target) dbs = ContractRun.ReadGame(target, settings);
else { Console.Error.WriteLine("--dbs <folder> or --target <game copy> is required."); return 2; }
var main = T3Db.Load((byte[])dbs["nhl_game"].Clone(), ContractRun.MetaPath);

List<Contract> contracts;
if (Arg("--contracts") is { } cf)
    contracts = cf.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ContractStore.FromCsv(File.ReadAllText(cf)) : ContractStore.Load(cf);
else
{
    var nhl27 = Nhl27Contracts.Load(Arg("--nhl27") ?? Nhl27Contracts.DefaultCsv);
    var pre = Nhl27Contracts.Prefill(nhl27, main);
    contracts = pre.Contracts;
    Console.WriteLine($"NHL 27 signed players: {pre.Nhl27Signed}; matched {pre.Matched} ({pre.Matched - pre.ByName} by game_id, {pre.ByName} by name+birth); unmatched {pre.Unmatched.Count}; with clauses {contracts.Count(c => c.Clauses.Count > 0)}");
    Console.WriteLine("Unmatched (first 25): " + string.Join(", ", pre.Unmatched.Take(25).Select(u => $"{u.Name} ({u.Note})")));
    if (Arg("--save") is { } save) { ContractStore.Save(contracts, save); Console.WriteLine("Saved " + save); }
}

List<TeamCap>? teams = null;
if (args.Contains("--teams") && Nhl27Contracts.FindTable(TeamCap.Table) is { } teamCsv)
{
    teams = TeamCap.LoadNhl27(teamCsv).Where(t => t.Team is >= 0 and < 32).ToList();
    Console.WriteLine($"Teams: {teams.Count} clubs from NHL 27 ({teams.Count(t => t.DeadCap.Any(v => v > 0) || t.Retained.Any(v => v > 0))} with dead cap or retained salary)");
}
var plans = ContractRun.Plan(dbs, contracts, map, null, teams);
foreach (var p in plans) Console.WriteLine($"{p.Db}: {p.Changes.Count} cells, {p.Changes.Select(c => c.GameId).Distinct().Count()} players");

// Read back named samples from every changed database.
foreach (var p in plans)
{
    var db = T3Db.Load((byte[])p.Bytes.Clone(), ContractRun.MetaPath);
    var now = ContractDb.Read(db, map, p.Db).ToDictionary(c => c.GameId);
    foreach (var name in (Arg("--show") ?? "Connor McDavid,Auston Matthews").Split(','))
    {
        var c = contracts.FirstOrDefault(x => x.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (c is null || !now.TryGetValue(c.GameId, out var r)) continue;
        Console.WriteLine($"  {p.Db,-18} {r.Name,-22} cap ${r.CapHit:N0} x{r.Years} 2way {(r.TwoWay ? 1 : 0)} ext ${r.ExtCapHit:N0} x{r.ExtYears} ufa {r.YearUfa}  clauses(suite) {Clause.FormatList(c.Clauses)}");
    }
}
if (teams is not null)
    foreach (var p in plans)
    {
        var db = T3Db.Load((byte[])p.Bytes.Clone(), ContractRun.MetaPath);
        foreach (var tc in TeamCap.Read(db, p.Db).Where(t => t.Team is 11 or 25 or 27))
            Console.WriteLine($"  {p.Db,-18} team {tc.Team,2} dead {TeamCap.FormatList(tc.DeadCap),-22} retained {TeamCap.FormatList(tc.Retained),-34} count {TeamCap.FormatList(tc.RetainedCount)}");
    }
if (Arg("--out") is { } outDir)
{
    Directory.CreateDirectory(outDir);
    foreach (var p in plans) File.WriteAllBytes(Path.Combine(outDir, p.Db + ".db"), p.Bytes);
    Console.WriteLine($"Wrote {plans.Count} database(s) to {outDir}");
}
if (Arg("--target") is { } t && WriteGate.Allowed(args))
    foreach (var line in ContractRun.Write(t, settings, plans)) Console.WriteLine(line);
else if (Arg("--target") is not null) Console.WriteLine("Dry run (add --apply to write the game copy).");
return 0;
