// Commentary: NHL 27 surname calls into NHL 24 (main commentary), by donor surnames. See docs\COMMENTARY_FEASIBILITY.md.
//
//   Commentary --make-plan --target <game> --nhl27 <PS5 app0> --nhl27-csv <csv dir> [--plan data\commentary_plan.json]
//       Read only. Finds the live roster's NHL players with no NHL 24 call and writes the plan:
//       - db-reuse: an NHL 24 player with the same surname is voiced -> take his audioid + surnameid (no audio).
//       - donor:    NHL 27 has the surname's takes -> pick an NHL 24 surname nobody on the live roster or in NHL 27 uses,
//                   replace that surname's takes (every surname wave) with the NHL 27 takes, give the player the donor's
//                   audioid + surnameid (the code maps those to the surname keyword).
//       - silent:   neither.
//   Commentary --selftest --target <game> [--nhl27 <PS5 app0>]
//       Read only, in memory: every surname wave rebuilt with no change is byte-identical (chunk, RES, EBX), the stored
//       audio blocks re-encode byte-identical, and a swapped take re-reads correctly.
//   Commentary --target <game> --nhl27 <PS5 app0> [--plan ...] [--cas data:02:03] [--apply [--restore-check]]
//       Without --apply: dry run (what would change). With --apply: refuses while the game runs, backs up (TOCs + CAS
//       lengths), appends the rebuilt wave chunks (stored-only) into the --cas file, the wave EBX/RES copies into their own
//       CAS, writes the five roster DBs, then re-reads everything. --restore-check also restores the backup, checks the
//       game is identical to it, and applies again. Re-running on an applied game changes nothing (idempotent).
//   Commentary --restore <backup folder> --target <game> --apply
using System.Text;
using System.Text.Json;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;
using Suite.Sounds;

void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var settings = SuiteSettings.Load();
var target = Opt("--target") is { } t0 ? Path.GetFullPath(t0).TrimEnd(Path.DirectorySeparatorChar) : throw new ArgumentException("--target <game> is required");
var planPath = Path.GetFullPath(Opt("--plan") ?? Path.Combine("data", "commentary_plan.json"));
foreach (var original in new[] { settings.Nhl24GameDir, settings.Nhl24BaseDir, settings.Nhl24UpdateDir, settings.Nhl24ReadView })
    if (!string.IsNullOrWhiteSpace(original) && string.Equals(Path.GetFullPath(original).TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase)
        && (args.Contains("--apply") || args.Contains("--restore")))
        { Say("REFUSED: the original game."); return 1; }

if (Opt("--restore") is { } restoreFrom)
{
    if (!WriteGate.Allowed(args)) { Say("REFUSED: --restore needs --target and --apply."); return 1; }
    if (!string.Equals(Path.GetFullPath(ApplyTarget.LoadSnapshot(restoreFrom).Game).TrimEnd(Path.DirectorySeparatorChar), target, StringComparison.OrdinalIgnoreCase))
        { Say("REFUSED: that backup belongs to a different game."); return 1; }
    if (ApplyTarget.RunningGame() is { } busy) { Say("REFUSED: " + busy + " is running"); return 1; }
    ApplyTarget.RestoreFromBackup(target, restoreFrom);
    var diffs = ApplyTarget.Compare(target, restoreFrom);
    Say($"restored: {(diffs.Count == 0 ? "identical to the backup" : "DIFFERENCES: " + string.Join("; ", diffs))}");
    return diffs.Count == 0 ? 0 : 1;
}

Oodle.Ensure(settings);
var meta = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game-meta.xml");
var dbNames = RosterSyncRun.Databases.ToDictionary(d => d, RosterSyncRun.LegacyName);
static bool SurnameWave(string n) => n.Contains("surname", StringComparison.OrdinalIgnoreCase);
static string Short(string wave) => wave[(SpeechCatalog.Root.Length + "soundwaves/".Length)..];
static string Fold(string s) => new string(s.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
static string LastWord(string text) => text.Split([' ', '.', ',', '/', '\\', '!', '?'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

Nhl24SoundAssets Load24(string game) => new(BundleIndex.Build(game));

// NHL 24 wave (folder-less short name) -> NHL 27 waves to take the call from, best first. "+" = phrase take ("to X").
var sources = new Dictionary<string, string[]>
{
    ["pa_player_surname_pxsend"] = ["pa_player_surname_pxsend", "pa_player_surname_pxspos", "pm_player_surname_level_2", "pm_player_surname_level_1"],
    ["pa_player_surname_pxspos"] = ["pa_player_surname_pxspos", "pa_player_surname_pxsend", "pm_player_surname_level_1", "pm_player_surname_level_2"],
    ["pm_player_surname_pxsend"] = ["pm_player_surname_level_2", "pm_player_surname_level_1", "pm_player_surname_level_3", "pa_player_surname_pxsend", "pa_player_surname_pxspos"],
    ["pm_player_surname_pxspos"] = ["pm_player_surname_level_1", "pm_player_surname_level_2", "pa_player_surname_pxspos", "pa_player_surname_pxsend"],
    ["pm_pxby_player_surname"] = ["+pm_pxby_player_surname_level_1", "pm_player_surname_level_1", "pa_player_surname_pxspos", "pa_player_surname_pxsend"],
    ["pm_pxby_goalie_l2_surname"] = ["+pm_pxby_goalie_surname_level_2", "+pm_pxby_player_surname_level_1", "pm_player_surname_level_2", "pa_player_surname_pxspos"],
    ["pm_pxto_player_surname"] = ["+pm_pxto_player_surname", "pm_player_surname_level_1", "pa_player_surname_pxspos", "pa_player_surname_pxsend"],
    ["pm_pxto_player_l2_surname"] = ["+pm_pxto_player_surname", "pm_player_surname_level_2", "pa_player_surname_pxspos", "pa_player_surname_pxsend"],
    ["ps_player_surname_pxsend"] = ["pa_player_surname_pxsend", "pa_player_surname_pxspos", "pm_player_surname_level_2"],
    ["ps_player_surname_pxspos"] = ["pa_player_surname_pxspos", "pa_player_surname_pxsend", "pm_player_surname_level_1"],
};
static string Base(string wave) => wave.Split('/')[^1];

// ======================================================================= make-plan
if (args.Contains("--make-plan"))
{
    var csvDir = Opt("--nhl27-csv") ?? throw new ArgumentException("--nhl27-csv <dir> is required");
    var app0 = Opt("--nhl27") ?? throw new ArgumentException("--nhl27 <PS5 app0> is required");
    Say("indexing NHL 24 speech");
    var cat24 = SpeechCatalog.Load(Load24(target), SurnameWave);
    Say("indexing NHL 27 speech");
    var c27 = SpeechCatalog.Load(Ps5SoundAssets.Build(app0), SurnameWave);

    var live = T3Db.Load(new LegacyFiles(target, settings).Read(dbNames["nhl_game"]), meta);
    var stockPath = Path.Combine(SuitePaths.Local, "dbwork", "stock", dbNames["nhl_game"]);
    var stock = T3Db.Load(File.ReadAllBytes(stockPath), meta);
    var bioL = live.Tables["exhibitionplayerbiotable"]; var bioS = stock.Tables["exhibitionplayerbiotable"];
    var stockAudio = new HashSet<long>();
    var stockBy = new Dictionary<string, List<(long Audio, long Surname, string Name)>>();
    for (int r = 0; r < bioS.InUse; r++)
    {
        long a = stock.GetInt(bioS, r, "audioid");
        if (a <= 0) continue;
        stockAudio.Add(a);
        var ln = Fold(stock.GetString(bioS, r, "lastname"));
        if (!stockBy.TryGetValue(ln, out var l)) stockBy[ln] = l = [];
        l.Add((a, stock.GetInt(bioS, r, "surnameid"), stock.GetString(bioS, r, "firstname") + " " + stock.GetString(bioS, r, "lastname")));
    }
    // Surnames a donor must not have: every NHL club player on the live roster, and every NHL 27 player (below).
    var liveNames = new HashSet<string>();
    for (int r = 0; r < bioL.InUse; r++)
        if (live.GetInt(bioL, r, "team") is >= 0 and <= 31) liveNames.Add(Fold(live.GetString(bioL, r, "lastname")));
    {
        var lines = File.ReadAllLines(Path.Combine(csvDir, "exhibitionplayerbiotable.csv"), Encoding.Latin1);
        var head = lines[0].Split(',').Select(h => h.Trim('"', '﻿')).ToList();
        int li = head.IndexOf("lastname");
        foreach (var l in lines.Skip(1)) { var v = l.Split(','); if (v.Length > li) liveNames.Add(Fold(v[li].Trim('"'))); }
    }

    // Surname of each NHL 24 keyword (every take of it must say the same surname).
    var kwText = new Dictionary<int, HashSet<string>>();
    foreach (var c in cat24.Clips)
        foreach (var k in c.Keywords)
        {
            if (!kwText.TryGetValue(k, out var s)) kwText[k] = s = [];
            s.Add(Fold(LastWord(c.Text)));
        }
    var donors = new List<(int Kw, string Surname, long Audio, long SurnameId, string Player, int Waves, List<string> Holders)>();
    var kwsOfSurname = kwText.Where(kv => kv.Value.Count == 1).GroupBy(kv => kv.Value.First()).ToDictionary(g => g.Key, g => g.Count());
    foreach (var (kw, texts) in kwText)
    {
        if (texts.Count != 1) continue;
        var sn = texts.First();
        if (kwsOfSurname[sn] != 1) continue;   // a surname recorded under several keywords: which one the ids select is unknown
        if (sn.Length < 3 || liveNames.Contains(sn) || !stockBy.TryGetValue(sn, out var ps)) continue;
        // Live players holding the donor's ids keep them but would be called by the new name: only free agents
        // (team -1, not legends) are accepted, and they are listed in the plan.
        var holders = new List<string>();
        bool blocked = false;
        for (int r = 0; r < bioL.InUse && !blocked; r++)
            if (ps.Any(x => live.GetInt(bioL, r, "audioid") == x.Audio || live.GetInt(bioL, r, "surnameid") == x.Surname))
            {
                if (live.GetInt(bioL, r, "team") != -1 || live.GetInt(bioL, r, "islegend") != 0) blocked = true;
                else holders.Add($"{live.GetString(bioL, r, "firstname")} {live.GetString(bioL, r, "lastname")} (free agent, game_id {live.GetInt(bioL, r, "game_id")})");
            }
        if (blocked) continue;
        if (ps.Select(x => x.Surname).Distinct().Count() != 1) continue;
        var takes = cat24.ByKeyword(kw).ToList();
        if (takes.Any(c => c.Wave.Contains("/eng_lcu_"))) continue;
        int waves = takes.Select(c => c.Wave).Distinct().Count();
        donors.Add((kw, sn, ps[0].Audio, ps[0].Surname, ps[0].Name, waves, holders));
    }
    donors = donors.OrderByDescending(d => d.Waves).ThenBy(d => d.Kw).ToList();
    Say($"{donors.Count} donor-safe NHL 24 surnames (ids held only by free agents, surname not on an NHL club or in NHL 27, no LCU takes)");

    var players = new List<object>();
    var silent = new List<string>();
    int nextDonor = 0;
    for (int r = 0; r < bioL.InUse; r++)
    {
        long team = live.GetInt(bioL, r, "team");
        long audio = live.GetInt(bioL, r, "audioid");
        if (team is < 0 or > 31 || (audio > 0 && stockAudio.Contains(audio))) continue;
        long gid = live.GetInt(bioL, r, "game_id");
        string first = live.GetString(bioL, r, "firstname"), last = live.GetString(bioL, r, "lastname");
        var ln = Fold(last);
        if (stockBy.TryGetValue(ln, out var same) && cat24.Clips.Any(c => c.Keywords.Length > 0 && Fold(LastWord(c.Text)) == ln))
        {
            players.Add(new { game_id = gid, name = $"{first} {last}", team, mode = "db-reuse", audioid = same[0].Audio, surnameid = same[0].Surname, from = same[0].Name });
            continue;
        }
        var own27 = c27.Clips.Where(c => Fold(LastWord(c.Text)) == ln).ToList();
        if (own27.Count == 0 || nextDonor >= donors.Count) { silent.Add($"{first} {last} ({gid})"); continue; }
        var d = donors[nextDonor];
        var takes = new List<object>();
        foreach (var dt in cat24.ByKeyword(d.Kw).Concat(cat24.Clips.Where(c => c.Keywords.Length == 0 && Fold(LastWord(c.Text)) == d.Surname)).DistinctBy(c => (c.Wave, c.VariationId)))
        {
            if (!sources.TryGetValue(Base(dt.Wave), out var prefs)) continue;
            SpeechCatalog.Clip? src = null;
            int used = takes.Count(x => ((dynamic)x).wave == dt.Wave);
            foreach (var p in prefs)
            {
                bool phrase = p.StartsWith('+');
                var cands = own27.Where(c => Base(c.Wave) == p.TrimStart('+') && (phrase ? c.Text.Contains(' ') : Fold(c.Text.TrimEnd('.', ' ')) == ln)).ToList();
                if (cands.Count > 0) { src = cands[used % cands.Count]; break; }
            }
            if (src is null) continue;
            takes.Add(new { wave = dt.Wave, variation = dt.VariationId, text24 = dt.Text, src_wave = src.Wave, src_variation = src.VariationId, text27 = src.Text, seconds = src.Seconds });
        }
        if (takes.Count == 0) { silent.Add($"{first} {last} ({gid}): NHL 27 takes do not fit any NHL 24 surname wave"); continue; }
        nextDonor++;
        players.Add(new { game_id = gid, name = $"{first} {last}", team, mode = "donor", audioid = d.Audio, surnameid = d.SurnameId,
            donor = new { surname = d.Surname, keyword = d.Kw, player = d.Player, free_agents_sharing_call = d.Holders }, takes });
    }
    var plan = new { generated = DateTime.Now, target, nhl27 = app0, players, silent };
    Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
    File.WriteAllText(planPath, JsonSerializer.Serialize(plan, jsonOptions));
    Say($"plan: {players.Count} players ({players.Count(p => ((dynamic)p).mode == "db-reuse")} db-reuse, {players.Count(p => ((dynamic)p).mode == "donor")} donor), {silent.Count} silent -> {planPath}");
    return 0;
}

// ======================================================================= selftest
if (args.Contains("--selftest"))
{
    Say("indexing NHL 24 speech (read only)");
    var a24 = Load24(target);
    var cat = SpeechCatalog.Load(a24, SurnameWave);
    int fails = 0;
    var tocs = TocChunkWriter.LoadTocs(target);
    foreach (var w in cat.Waves)
    {
        var chunk = cat.ChunkOf(w);
        var same = SpeechBankWriter.Rebuild(chunk, w.Res, w.Ebx, new Dictionary<uint, SpeechBankWriter.Replacement>());
        bool identical = same.Chunk.AsSpan().SequenceEqual(chunk) && same.Res.AsSpan().SequenceEqual(w.Res) && same.Ebx.AsSpan().SequenceEqual(w.Ebx);
        var where = TocChunkWriter.FindAll(tocs, w.ChunkId).First().Where;
        var stored = TocChunkWriter.ReadStored(target, where);
        var blocks = ChunkBlocks.Parse(stored).Blocks;
        bool storedSame = ChunkBlocks.StoreBlocks(chunk, blocks.Max(b => b.RawSize)).AsSpan().SequenceEqual(stored);
        if (!identical || !storedSame) fails++;
        Say($"  {Short(w.Name),-44} unchanged rebuild identical: {identical}; stored blocks re-encode identical: {storedSame} (blocks {blocks.Count}, type {blocks[0].Type:X2}, flags {blocks[0].Flags:X2}, raw {blocks[0].RawSize})");
    }
    // Swap: Stamkos's pa_pxsend take gets another take's audio; everything else must stay as it was.
    var wave = cat.Waves.First(w => Base(w.Name) == "pa_player_surname_pxsend" && w.Name.Contains("/eng_inxp/"));
    var takes = cat.Clips.Where(c => c.Wave == wave.Name).ToList();
    var victim = takes.First(c => c.Text == "Stamkos"); var donorTake = takes.First(c => c.Text == "Toews");
    var chunk0 = cat.ChunkOf(wave);
    var sps = chunk0.AsSpan(donorTake.Offset, SpsStream.TryRead(chunk0, donorTake.Offset)!.Length).ToArray();
    var swapped = SpeechBankWriter.Rebuild(chunk0, wave.Res, wave.Ebx, new Dictionary<uint, SpeechBankWriter.Replacement> { [victim.VariationId] = new(sps, donorTake.Seconds) });
    var bank = HarmonyBank.Parse(swapped.Res);
    var clips = bank.SpeechClips().ToDictionary(c => c.VariationId);
    var streams = SpsStream.ReadAll(swapped.Chunk).ToDictionary(s => s.Offset);
    bool ok = clips[victim.VariationId] is var v && streams.TryGetValue(v.Offset, out var s) && swapped.Chunk.AsSpan(v.Offset, s.Length).SequenceEqual(sps)
        && Math.Abs(v.Seconds - donorTake.Seconds) < 1e-4
        && takes.Where(c => c.VariationId != victim.VariationId).All(c => clips[c.VariationId].Offset == c.Offset)
        && SpeechCatalog.ReadChunks(swapped.Ebx)[0].Size == swapped.Chunk.Length && bank.ChunkSize == swapped.Chunk.Length
        && swapped.Res.Length == wave.Res.Length && swapped.Ebx.Length == wave.Ebx.Length;
    if (!ok) fails++;
    Say($"  swap test (Stamkos take {victim.VariationId} <- Toews audio): {(ok ? "re-reads correctly; other takes unchanged; sizes updated" : "FAILED")}");
    Say(fails == 0 ? "selftest passed" : $"selftest: {fails} failure(s)");
    return fails == 0 ? 0 : 1;
}

// ======================================================================= apply (dry run unless --apply)
if (!File.Exists(planPath)) { Say($"no plan at {planPath}; run --make-plan first"); return 1; }
using var planDoc = JsonDocument.Parse(File.ReadAllText(planPath));
var planPlayers = planDoc.RootElement.GetProperty("players").EnumerateArray().ToList();
var app0Apply = Opt("--nhl27") ?? planDoc.RootElement.GetProperty("nhl27").GetString()!;
var casArg = (Opt("--cas") ?? "data:02:03").Split(':');
var into = new TocChunkWriter.CasTarget(casArg[0].Equals("patch", StringComparison.OrdinalIgnoreCase), int.Parse(casArg[1]), int.Parse(casArg[2]));

// Takes per NHL 24 wave: VariationId -> (NHL 27 wave, VariationId, seconds).
var byWave = new Dictionary<string, Dictionary<uint, (string W27, uint V27, double Sec)>>();
foreach (var p in planPlayers.Where(p => p.GetProperty("mode").GetString() == "donor"))
    foreach (var tk in p.GetProperty("takes").EnumerateArray())
    {
        var w = tk.GetProperty("wave").GetString()!;
        if (!byWave.TryGetValue(w, out var m)) byWave[w] = m = [];
        m[tk.GetProperty("variation").GetUInt32()] = (tk.GetProperty("src_wave").GetString()!, tk.GetProperty("src_variation").GetUInt32(), tk.GetProperty("seconds").GetDouble());
    }

Say($"plan: {planPlayers.Count} players, {byWave.Sum(w => w.Value.Count)} takes in {byWave.Count} waves; chunks go into {into}");
Ps5SoundAssets? a27 = null;
SpeechCatalog? cat27 = null;
if (byWave.Count > 0)
{
    Say("indexing NHL 27 speech (read only)");
    a27 = Ps5SoundAssets.Build(app0Apply);
    cat27 = SpeechCatalog.Load(a27, SurnameWave);
}
var chunk27 = new Dictionary<Guid, byte[]>();
byte[] Sps27(string wave, uint v)
{
    var w = cat27!.Waves.First(x => x.Name == wave);
    if (!chunk27.TryGetValue(w.ChunkId, out var raw)) chunk27[w.ChunkId] = raw = cat27.ChunkOf(w);
    var c = cat27.Clips.First(x => x.Wave == wave && x.VariationId == v);
    return raw.AsSpan(c.Offset, SpsStream.TryRead(raw, c.Offset)!.Length).ToArray();
}

// Builds everything to write from the game's current state. Already-applied waves/cells come out as no work.
(List<(SpeechCatalog.Wave W, byte[] Chunk, byte[] OldChunk, SpeechBankWriter.Result R)> Waves, Dictionary<string, byte[]> Dbs, int Cells, BundleIndex Index) Build()
{
    var a24 = Load24(target);
    var cat = SpeechCatalog.Load(a24, SurnameWave);
    var waves = new List<(SpeechCatalog.Wave, byte[], byte[], SpeechBankWriter.Result)>();
    foreach (var (wname, reps) in byWave)
    {
        var w = cat.Waves.FirstOrDefault(x => x.Name == wname) ?? throw new InvalidDataException($"wave not in game: {wname}");
        var chunk = cat.ChunkOf(w);
        var current = cat.Clips.Where(c => c.Wave == wname).ToDictionary(c => c.VariationId);
        var todo = new Dictionary<uint, SpeechBankWriter.Replacement>();
        foreach (var (v, src) in reps)
        {
            var sps = Sps27(src.W27, src.V27);
            if (!current.TryGetValue(v, out var cur)) throw new InvalidDataException($"take {v} not in {wname}");
            bool done = SpsStream.TryRead(chunk, cur.Offset) is { } s && s.Length == sps.Length && chunk.AsSpan(cur.Offset, s.Length).SequenceEqual(sps);
            if (!done) todo[v] = new(sps, src.Sec);
        }
        if (todo.Count == 0) continue;
        waves.Add((w, chunk, chunk, SpeechBankWriter.Rebuild(chunk, w.Res, w.Ebx, todo)));
    }
    var lf = new LegacyFiles(target, settings);
    var dbs = new Dictionary<string, byte[]>();
    int cells = 0;
    foreach (var (db, legacy) in dbNames)
    {
        var bytes = lf.Read(legacy);
        var t3 = T3Db.Load((byte[])bytes.Clone(), meta);
        int n = 0;
        foreach (var tb in t3.Tables.Values.Where(x => x.Fields.ContainsKey("game_id") && x.Fields.ContainsKey("audioid") && x.Fields.ContainsKey("surnameid")))
            for (int r = 0; r < tb.InUse; r++)
            {
                long gid = t3.GetInt(tb, r, "game_id");
                var p = planPlayers.FirstOrDefault(x => x.GetProperty("game_id").GetInt64() == gid);
                if (p.ValueKind == JsonValueKind.Undefined) continue;
                foreach (var f in new[] { "audioid", "surnameid" })
                {
                    long val = p.GetProperty(f).GetInt64();
                    if (t3.GetInt(tb, r, f) != val) { t3.SetInt(tb, r, f, val); n++; }
                }
            }
        if (n == 0) continue;
        var saved = t3.Save();
        if (saved.Length != bytes.Length) throw new InvalidOperationException($"{db}: size changed");
        T3Db.Load((byte[])saved.Clone(), meta);
        dbs[legacy] = saved;
        cells += n;
    }
    return (waves, dbs, cells, a24.Index);
}

var work = Build();
foreach (var (w, _, old, r) in work.Waves)
    Say($"  {Short(w.Name),-44} {r.Replaced,3} take(s); chunk {old.Length:N0} -> {r.Chunk.Length:N0} bytes");
Say($"  databases: {work.Dbs.Count} to write, {work.Cells} cells");
if (work.Waves.Count == 0 && work.Dbs.Count == 0) { Say("nothing to do (already applied)"); return 0; }
if (!WriteGate.Allowed(args)) { Say("dry run (pass --apply with --target to write)"); return 0; }

if (ApplyTarget.RunningGame() is { } running) { Say("REFUSED: " + running + " is running"); return 1; }
long need = work.Waves.Sum(x => x.R.Chunk.Length + x.R.Chunk.Length / 262144 * 8 + 8);
var intoPath = BundleIndex.CasPath(target, into.Patch, into.Catalog, into.Cas);
if (new FileInfo(intoPath).Length + need > uint.MaxValue) { Say($"REFUSED: {intoPath} has no room for {need:N0} bytes"); return 1; }
var backup = ApplyTarget.Backup(target);
Say("backup: " + backup);

bool WriteAll((List<(SpeechCatalog.Wave W, byte[] Chunk, byte[] OldChunk, SpeechBankWriter.Result R)> Waves, Dictionary<string, byte[]> Dbs, int Cells, BundleIndex Index) wk, string label)
{
    foreach (var (w, _, old, r) in wk.Waves)
    {
        var res = TocChunkWriter.Replace(target, w.ChunkId, r.Chunk, old, into, storeUncompressed: true);
        foreach (var l in res.Log) SuiteLog.Write("commentary: " + l);
    }
    var jobs = new List<BundleEntryWriter.Job>();
    foreach (var (w, _, _, r) in wk.Waves)
    {
        foreach (var e in wk.Index.EbxByName[w.Name])
        {
            var raw = Nhl24SoundAssets.Decode(BundleIndex.ReadBytes(e.Data));
            if (!raw.AsSpan().SequenceEqual(w.Ebx)) throw new InvalidDataException($"{w.Name}: EBX copies differ; refusing.");
            jobs.Add(new(e.Data, e.Bundle, e.Sha1At, r.Ebx));
        }
        foreach (var e in wk.Index.ResByName[w.Name])
        {
            var raw = Nhl24SoundAssets.Decode(BundleIndex.ReadBytes(e.Data));
            if (!raw.AsSpan().SequenceEqual(w.Res)) throw new InvalidDataException($"{w.Name}: RES copies differ; refusing.");
            jobs.Add(new(e.Data, e.Bundle, e.Sha1At, r.Res));
        }
    }
    foreach (var l in BundleEntryWriter.Write(target, jobs)) SuiteLog.Write("commentary: " + l);
    if (wk.Dbs.Count > 0) foreach (var l in new LegacyFiles(target, settings).Replace(wk.Dbs)) SuiteLog.Write("commentary: " + l);

    // Read back: a fresh build must find nothing left to do, every planned take must play the NHL 27 audio.
    var again = Build();
    var cat = SpeechCatalog.Load(Load24(target), SurnameWave);
    int bad = 0;
    foreach (var (wname, reps) in byWave)
    {
        var w = cat.Waves.First(x => x.Name == wname);
        var chunk = cat.ChunkOf(w);
        var clips = cat.Clips.Where(c => c.Wave == wname).ToDictionary(c => c.VariationId);
        foreach (var (v, src) in reps)
            if (!chunk.AsSpan(clips[v].Offset).StartsWith(Sps27(src.W27, src.V27))) bad++;
        var streams = SpsStream.ReadAll(chunk).ToDictionary(s => s.Offset);
        bad += clips.Values.Count(c => !streams.ContainsKey(c.Offset));
    }
    bool clean = again.Waves.Count == 0 && again.Dbs.Count == 0 && bad == 0;
    Say($"  {label}: read back {(clean ? "OK (every planned take plays its NHL 27 audio; nothing left to do)" : $"PROBLEMS: {again.Waves.Count} waves, {again.Dbs.Count} DBs still to do, {bad} bad takes")}");
    return clean;
}

if (!WriteAll(work, "applied")) { ApplyTarget.RestoreFromBackup(target, backup); Say("FAILED; restored"); return 1; }
if (args.Contains("--restore-check"))
{
    ApplyTarget.RestoreFromBackup(target, backup);
    var diffs = ApplyTarget.Compare(target, backup);
    Say("  restored: " + (diffs.Count == 0 ? "identical to the backup" : "DIFFERENCES: " + string.Join("; ", diffs)));
    if (diffs.Count > 0) return 1;
    if (!WriteAll(Build(), "re-applied")) { ApplyTarget.RestoreFromBackup(target, backup); Say("FAILED; restored"); return 1; }
}
Say($"done. backup: {backup}  (undo: Commentary --restore \"{backup}\" --target \"{target}\" --apply)");
return 0;
