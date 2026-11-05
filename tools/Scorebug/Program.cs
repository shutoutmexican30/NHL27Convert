// Scorebug check
//     For each style: every schema key is in the movie's string pool, and a settings rewrite reads back with every byte
//     outside the settings block unchanged.
// Scorebug read <game>
//     Which scorebug the game's scoreclock.gfx is (stock or a custom style) and its settings.
// Scorebug apply <espn|prime> [settings text] --target <game> --apply [--restore-check]
//     Writes the scorebug with the suite's writer (backup, write, read back), then checks that every other legacy file
//     of that index still reads the same. With --restore-check it then restores the backup and checks the game is back
//     to stock. Without --apply and --target it is a dry run.
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Scorebug;

void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
var settings = SuiteSettings.Load();

if (args.Length >= 1 && args[0] == "check")
{
    int bad = 0;
    foreach (var style in ScorebugStyle.All)
    {
        var movie = File.ReadAllBytes(style.MoviePath);
        var missing = ScorebugSettings.MissingKeys(style, movie);
        Say($"{style.Name}: {new ScorebugSettings(style).All.Count} settings, missing from the movie: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");
        bad += missing.Count;
        Say($"  stock block: \"{ScorebugMovie.ReadSettings(movie)}\"");
        var s = new ScorebugSettings(style);
        foreach (var setting in s.All)
            s.Set(setting, setting.Kind switch
            {
                SettingKind.Number => (setting.Min + (setting.Max - setting.Min) * 0.37).ToString(System.Globalization.CultureInfo.InvariantCulture),
                SettingKind.Colour => "12AB34",
                SettingKind.Flag => setting.Default == "1" ? "0" : "1",
                _ => "TEST " + setting.Key,
            });
        s.TeamColours.Add(("UTA", "6CACE4", "010101"));
        var text = s.ToSettingsString();
        var rewritten = ScorebugMovie.WriteSettings(movie, text);
        var back = ScorebugMovie.ReadSettings(rewritten);
        var a = ScorebugMovie.Unpack(movie).Body; var b = ScorebugMovie.Unpack(rewritten).Body;
        int diffs = 0, first = -1, last = -1;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { diffs++; if (first < 0) first = i; last = i; }
        var round = new ScorebugSettings(style); round.FromSettingsString(back);
        bool ok = back == text && a.Length == b.Length && last - first < ScorebugMovie.BlockSize && round.ToSettingsString() == text;
        Say($"  all {s.All.Count} changed ({text.Length} chars): read back {(back == text ? "identical" : "DIFFERENT")}, parsed back {(round.ToSettingsString() == text ? "identical" : "DIFFERENT")}, " +
            $"{diffs} bytes differ, all inside {first}..{last} (block {ScorebugMovie.BlockSize} bytes); movie {movie.Length:N0} -> {rewritten.Length:N0} bytes  {(ok ? "OK" : "FAIL")}");
        if (!ok) bad++;
    }
    // Packs: each one given reads, and exporting it again reads back with the same style, values and team colours.
    foreach (var path in args.Skip(1))
    {
        var pack = ScorebugPack.Read(path);
        var s = new ScorebugSettings(pack.Style);
        s.FromStored(pack.Manifest.Scorebug);
        s.Set(s.All[0], "123");
        s.TeamColours.Add(("UTA", "6CACE4", "010101"));
        var tmp = Path.Combine(Path.GetTempPath(), "cims-pack-check" + ScorebugPack.Extension);
        ScorebugPack.Write(tmp, pack.Manifest.Name!, "check", s, pack.Preview);
        var back = ScorebugPack.Read(tmp);
        var s2 = new ScorebugSettings(back.Style);
        s2.FromStored(back.Manifest.Scorebug);
        bool ok = back.Style == pack.Style && s2.ToSettingsString() == s.ToSettingsString() && (back.Preview?.Length ?? 0) == (pack.Preview?.Length ?? 0);
        Say($"pack {Path.GetFileName(path)}: \"{pack.Manifest.Name}\" by {pack.Manifest.Author}, {pack.Style.Name}, {pack.Manifest.Scorebug?.Values.Count ?? 0} values, " +
            $"preview {(pack.Preview is null ? "none" : pack.Preview.Length + " bytes")}, {pack.Manifest.Mods.Count} mods; re-export reads back \"{s2.ToSettingsString()}\"  {(ok ? "OK" : "FAIL")}");
        File.Delete(tmp);
        if (!ok) bad++;
    }
    return bad == 0 ? 0 : 1;
}

if (args.Length >= 3 && args[0] == "logos")
{
    foreach (var t in ScorebugTeams.All) Say($"  {t.Id,2} {t.Abbr} {t.Name}  {t.Primary}/{t.Secondary}");
    Say($"{ScorebugTeams.ExportLogos(args[1], args[2], Say)} logo(s) written to {args[2]}; {Directory.GetFiles(args[2], "t*.png").Length} there now");
    return 0;
}

if (args.Length >= 2 && args[0] == "read")
{
    var (style, text, bytes) = ScorebugApply.Read(args[1], settings);
    Say(style is null ? $"scoreclock.gfx: {bytes:N0} bytes, no settings block (stock or another mod)" : $"scoreclock.gfx: {bytes:N0} bytes, {style.Name}, settings \"{text}\"");
    var lf = new LegacyFiles(args[1], settings);
    foreach (var n in ScorebugApply.GameFiles) Say($"  {n}: {lf.Read(n).Length:N0} bytes");
    return 0;
}

// get <game> <legacyName> <outFile> — extract one legacy file (for comparing base game vs update overlays)
if (args.Length >= 4 && args[0] == "get")
{
    var bytes = new LegacyFiles(args[1], settings).Read(args[2]);
    File.WriteAllBytes(args[3], bytes);
    Say($"{args[2]}: {bytes.Length:N0} bytes -> {args[3]}");
    return 0;
}

// find <game> <substring> — every legacy file whose name contains it (same index as the scorebug's)
if (args.Length >= 3 && args[0] == "find")
{
    var lf = new LegacyFiles(args[1], settings);
    foreach (var n in lf.Names(ScorebugApply.GameFiles[0]).Where(n => n.Contains(args[2], StringComparison.OrdinalIgnoreCase)))
        Say($"  {n}");
    return 0;
}

// findindex <game> <substring> — scan every chunk of every TOC for one whose decoded bytes hold the text; print its
// id, size and the printable strings inside (to discover which legacy index / collector chunk holds a file).
if (args.Length >= 3 && args[0] == "findindex")
{
    var game = args[1];
    var needle = System.Text.Encoding.ASCII.GetBytes(args[2]);
    Oodle.Ensure(settings);
    var tocs = new[] { "Patch", "Data" }.Select(s => Path.Combine(game, s)).Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateFiles(d, "*.toc", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        .Select(TocFile.Load).ToList();
    var seen = new HashSet<Guid>();
    foreach (var toc in tocs)
        foreach (var id in toc.Chunks)
        {
            if (!seen.Add(id)) continue;
            var r = tocs.Select(t => t.Find(id)).FirstOrDefault(x => x is not null);
            if (r is null || r.Size > 8_000_000) continue;
            var cas = Path.Combine(game, r.Patch ? "Patch" : "Data", "Gen4b", "superbundlelayout", $"nhl_installpackage_{r.Catalog:D2}", $"cas_{r.Cas:D2}.cas");
            byte[] raw;
            try
            {
                using var fs = new FileStream(cas, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Position = r.Offset;
                var b = new byte[r.Size];
                fs.ReadExactly(b);
                raw = ChunkBlocks.Parse(b).Decode();
            }
            catch { continue; }
            if (raw.AsSpan().IndexOf(needle) < 0) continue;
            Say($"chunk {id} in {Path.GetFileName(toc.Path)}: {raw.Length:N0} bytes, strings:");
            for (int p = 0; p < raw.Length;)
            {
                int end = Array.IndexOf(raw, (byte)0, p);
                if (end < 0) end = raw.Length;
                if (end - p >= 4 && end - p <= 260)
                {
                    var s = System.Text.Encoding.ASCII.GetString(raw, p, end - p);
                    if (s.All(c => c >= 0x20 && c <= 0x7E) && (s.Contains('/') || s.Contains('.'))) Say($"  {s}");
                }
                p = end + 1;
            }
        }
    return 0;
}

// restore <game> [backupDir] — put the game back to a backup (default: the newest one under the suite's backup folder).
if (args.Length >= 2 && args[0] == "restore")
{
    var game = args[1];
    var backup = args.Length >= 3 ? args[2]
        : Directory.GetDirectories(Path.Combine(SuitePaths.Local, "backups")).Order().LastOrDefault()
            ?? throw new InvalidDataException("No backups found.");
    ApplyTarget.RestoreFromBackup(game, backup);
    var left = ApplyTarget.Compare(game, backup);
    Say($"restored from {backup}: {(left.Count == 0 ? "identical to the backup" : string.Join("; ", left))}");
    return left.Count == 0 ? 0 : 1;
}

if (args.Length >= 2 && args[0] == "apply")
{
    var style = ScorebugStyle.ById(args[1]);
    var s = new ScorebugSettings(style);
    if (args.Length > 2 && !args[2].StartsWith("--")) s.FromSettingsString(args[2]);
    Say($"{style.Name}: settings \"{s.ToSettingsString()}\"");
    if (!WriteGate.Allowed(args)) { Say("dry run (give --target <game> --apply to write)"); return 0; }
    var target = args[Array.IndexOf(args, "--target") + 1];
    var run = settings;
    run.ApplyTargetDir = target;   // this run only; settings.json is not saved

    // Every other file of the swf_assets index, before: they must read the same afterwards.
    var before = new LegacyFiles(target, settings);
    var others = before.Names(ScorebugApply.GameFiles[0]).Except(ScorebugApply.GameFiles).ToList();
    var hashes = others.ToDictionary(n => n, n => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(before.Read(n))));

    var (ok, msg, backup) = ScorebugApply.Apply(run, s, Say);
    Say(msg);
    if (!ok) return 1;
    var after = new LegacyFiles(target, settings);
    var changed = others.Where(n => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(after.Read(n))) != hashes[n]).ToList();
    Say($"other files of the index: {others.Count - changed.Count} of {others.Count} read the same{(changed.Count > 0 ? "; CHANGED: " + string.Join(", ", changed.Take(5)) : "")}");
    var (rs, rt, _) = ScorebugApply.Read(target, settings);
    Say($"game now: {rs?.Name ?? "no custom scorebug"}, settings \"{rt}\"");
    if (changed.Count > 0 || rs != style || rt != s.ToSettingsString()) return 1;

    if (args.Contains("--restore-check"))
    {
        ApplyTarget.RestoreFromBackup(target, backup!);
        var left = ApplyTarget.Compare(target, backup!);
        var (ss, _, sb) = ScorebugApply.Read(target, settings);
        Say($"restored: {(left.Count == 0 ? "identical to the backup" : string.Join("; ", left))}; scoreclock.gfx {sb:N0} bytes, {ss?.Name ?? "stock"}");
        if (left.Count > 0 || ss is not null) return 1;
    }
    return 0;
}

Console.WriteLine("usage: Scorebug check | read <game> | apply <espn|prime> [settings] --target <game> --apply [--restore-check]");
return 2;
