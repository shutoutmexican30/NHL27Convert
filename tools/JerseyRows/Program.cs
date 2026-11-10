// JerseyRows --target <game copy> [--apply]          add the NHL 27 extra-kit rows (default dry run)
// JerseyRows --target <game copy> --remove [--apply] remove those added rows again (default dry run)
// NHL 24 ships adidas kit folders that no stockteamjerseys row points at, so the game never offers them; the Convert 27
// matcher puts NHL 27 alternates into some of them (variant_map.json extraKits, replaces24), where nobody would see
// them.  For every such folder this adds a jersey row (style 2 = adidas, variant = the folder's number) to each database
// that has the table (nhl_game, initial_experience): a copy of the team's own alternate row (variant 2, else its last
// non-default adidas row) with the new variant/fevariant/variantleadingscorer, the team's next jerseyindex and orderid,
// isdefault 0, and islightcolor from the NHL 27 kit's jersey colour sheet (decoded by the matcher / UniformRef).  A row
// the main database already has is copied as it is.  Rows go at the end of the team's block (the table is grouped by
// team), inside the table's spare slots (T3Db.InsertRecord).  Without --apply nothing reaches the game.
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
string[] words = "home away two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty twentyone twentytwo twentythree twentyfour twentyfive".Split(' ');
var dbNames = new[] { "nhl_game", "initial_experience" };
var raw = dbNames.ToDictionary(d => d, d => new LegacyFiles(target, settings).Read(RosterSyncRun.LegacyName(d)));
var dbs = raw.ToDictionary(kv => kv.Key, kv => T3Db.Load((byte[])kv.Value.Clone(), meta));

// Wanted: every NHL team's extraKit target folder (team index, variant, the NHL 27 kit that goes there).
var wanted = new List<(int Team, string Name, int Variant, string Folder27, string Kit27)>();
foreach (var t in VariantMap.Load().Teams.Where(t => t.Index is >= 0 and < 32))
    foreach (var k in t.ExtraKits)
        if (k.Replaces24 is { } f && Array.IndexOf(words, f) is var v and > 1) wanted.Add((t.Index, t.Name24, v, t.Nhl27.Folder, k.Nhl27));

int Find(T3Db d, T3Db.Table t, int team, long variant)
{
    for (int r = 0; r < t.InUse; r++)
        if (d.GetInt(t, r, "teamid") == team && d.GetInt(t, r, "style") == 2 && d.GetInt(t, r, "variant") == variant) return r;
    return -1;
}
// Light or dark: the main colour of the NHL 27 kit's jersey sheet (the matcher's decoded PNG); null when not decoded.
long? Light(string folder27, string kit)
{
    var png = Path.Combine(SuitePaths.Nhl27Images, SuitePaths.Flatten($"{TextureName.SkaterPrefix}nhl/{folder27}/adidas/{kit}/jersey_adidas_{folder27}_{kit}_color") + ".png");
    if (!File.Exists(png)) return null;
    var img = ImageIO.Load(png);
    var counts = new Dictionary<int, int>();
    for (int i = 0; i < img.Pixels.Length; i += 12)
        if (img.Pixels[i + 3] > 128)
        {
            int key = (img.Pixels[i] >> 4) << 8 | (img.Pixels[i + 1] >> 4) << 4 | img.Pixels[i + 2] >> 4;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
    int top = counts.MaxBy(kv => kv.Value).Key;
    double lum = (0.2126 * (top >> 8) + 0.7152 * ((top >> 4) & 15) + 0.0722 * (top & 15)) / 15;
    return lum > 0.6 ? 1 : 0;
}

var main = dbs["nhl_game"];
var report = new List<string>();
bool remove = args.Contains("--remove");
int changed = 0;
foreach (var name in dbNames)
{
    var d = dbs[name];
    var t = d.Tables["stockteamjerseys"];
    foreach (var w in wanted)
    {
        // This team's rows: one block in the table. Rows this tool adds always join at the end of it.
        var block = Enumerable.Range(0, t.InUse).Where(r => d.GetInt(t, r, "teamid") == w.Team).ToList();
        if (block.Count == 0 || block.Last() - block.First() + 1 != block.Count) throw new InvalidOperationException($"{name}: team {w.Team}'s jersey rows are not one block");
        int row = Find(d, t, w.Team, w.Variant);
        if (remove)
        {
            if (row < 0) continue;
            if (row != block.Last()) { report.Add($"{name}: {w.Name} '{words[w.Variant]}': row is not the team's last row, not an added row; left alone"); continue; }
            t = d.DeleteRecord(t, row);
            report.Add($"{name}: {w.Name} '{words[w.Variant]}' (NHL 27 '{w.Kit27}'): row removed");
            changed++;
            continue;
        }
        if (row >= 0) continue;
        var adidas = block.Where(r => d.GetInt(t, r, "style") == 2).ToList();
        int template = adidas.Where(r => d.GetInt(t, r, "variant") == 2).DefaultIfEmpty(adidas.LastOrDefault(r => d.GetInt(t, r, "isdefault") == 0, -1)).First();
        if (template < 0) { report.Add($"{name}: {w.Name} '{words[w.Variant]}': no adidas alternate row to copy, skipped"); continue; }
        long templateVariant = d.GetInt(t, template, "variant");
        int mainRow = name == "nhl_game" ? -1 : Find(main, main.Tables["stockteamjerseys"], w.Team, w.Variant);
        long jerseyIndex = block.Max(r => d.GetInt(t, r, "jerseyindex")) + 1, orderId = adidas.Max(r => d.GetInt(t, r, "orderid")) + 1;
        int at = block.Last() + 1;
        t = d.InsertRecord(t, at, template);
        if (mainRow >= 0)
        {
            // The main database already has this row: the same values.
            var mt = main.Tables["stockteamjerseys"];
            foreach (var f in t.Fields.Values.Where(f => f.Type == 3)) d.SetInt(t, at, f.Name, main.GetInt(mt, mainRow, f.Name));
            report.Add($"{name}: {w.Name} '{words[w.Variant]}' (NHL 27 '{w.Kit27}'): row copied from nhl_game");
        }
        else
        {
            foreach (var f in new[] { "variant", "fevariant", "variantleadingscorer" }) d.SetInt(t, at, f, w.Variant);
            d.SetInt(t, at, "jerseyindex", jerseyIndex);
            d.SetInt(t, at, "orderid", orderId);
            d.SetInt(t, at, "isdefault", 0);
            var light = Light(w.Folder27, w.Kit27);
            if (light is { } l) d.SetInt(t, at, "islightcolor", l);
            report.Add($"{name}: {w.Name} '{words[w.Variant]}' (NHL 27 '{w.Kit27}'): new row, a copy of its variant {templateVariant} row, " +
                $"jerseyindex {jerseyIndex}, orderid {orderId}, {(d.GetInt(t, at, "islightcolor") == 1 ? "light" : "dark")}{(light is null ? " (as the template)" : "")}");
        }
        changed++;
    }
}
foreach (var l in report) Say(l);
var output = new Dictionary<string, byte[]>();
foreach (var (name, d) in dbs)
{
    var saved = d.Save();
    if (saved.Length != raw[name].Length) throw new InvalidOperationException($"{name}: size changed");
    var check = T3Db.Load((byte[])saved.Clone(), meta);   // every CRC valid again
    Say($"{name}: stockteamjerseys {check.Tables["stockteamjerseys"].InUse} rows");
    if (!saved.AsSpan().SequenceEqual(raw[name])) output[RosterSyncRun.LegacyName(name)] = saved;
}
Say($"{changed} jersey row(s) to {(remove ? "remove" : "add")}");
if (!WriteGate.Allowed(args) || output.Count == 0) { Say(output.Count == 0 ? "nothing to write" : "dry run (pass --apply with --target to write)"); return 0; }

if (ApplyTarget.RunningGame() is { } running) { Say("REFUSED: " + running + " is running"); return 1; }
var backup = ApplyTarget.Backup(target);
Say("backup: " + backup);
foreach (var line in new LegacyFiles(target, settings).Replace(output)) SuiteLog.Write("jerseyrows: " + line);
var back = new LegacyFiles(target, settings);
var wrong = output.Where(kv => !back.Read(kv.Key).AsSpan().SequenceEqual(kv.Value)).Select(kv => kv.Key).ToList();
Say($"read back {(wrong.Count == 0 ? "identical" : "DIFFERENT: " + string.Join(", ", wrong))}");
if (wrong.Count > 0) { ApplyTarget.RestoreFromBackup(target, backup); Say("FAILED; restored"); return 1; }
Say("done");
return 0;
