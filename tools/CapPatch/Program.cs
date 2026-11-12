using System.Xml.Linq;
using Suite.Core;

// CapPatch: writes the salary-cap entries from data\salary_cap.json into a shadPS4 patch file (nhl24.xml).
//   dotnet run --project tools\CapPatch -- --eboot <game-UPDATE\eboot.bin> [--cap <salary_cap.json>]                (dry run)
//   dotnet run --project tools\CapPatch -- --eboot <...> --apply --target <user\patches\nhl24.xml or data\nhl24-patches.xml>
// The eboot is only read. Every site must hold its stock value or nothing is written. A changed target is backed up
// first to <target>.before-cap-<season> (timestamped if that exists), then read back and checked.

string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

string? ebootPath = Arg("--eboot");
if (ebootPath is null) { Console.Error.WriteLine("--eboot <path to game-UPDATE eboot.bin> is required."); return 2; }
var patch = SalaryCapPatch.Load(File.ReadAllText(Arg("--cap") ?? SalaryCapPatch.ShippedPath));
byte[] eboot = File.ReadAllBytes(ebootPath);

var problems = patch.Verify(eboot);
foreach (var p in problems) Console.Error.WriteLine("REFUSED: " + p);
if (problems.Count > 0) return 1;

Console.WriteLine($"{patch.Name}: {string.Join(", ", patch.Values.Select(v => $"{v.Key} {v.Value:N0}"))}");
foreach (var s in patch.Sites)
    Console.WriteLine($"  {s.Group,-24} file {EbootPatches.Hex(s.FileOffset)} addr {EbootPatches.Hex(EbootPatches.RuntimeAddress(s.FileOffset, patch.CodeFileOffset))} {s.Field,-9} {s.Stock,12:N0} -> {patch.Values[s.Field],12:N0}");

if (!WriteGate.Allowed(args)) { Console.WriteLine("Dry run (add --apply --target <nhl24.xml> to write)."); return 0; }

string target = Arg("--target")!;
var doc = XDocument.Load(target, LoadOptions.None);
string before = doc.Declaration + Environment.NewLine + doc.ToString();
patch.ApplyTo(doc);
string after = doc.Declaration + Environment.NewLine + doc.ToString();
if (after == before) Console.WriteLine("Target already up to date.");
else
{
    string season = patch.Name.Split(' ').Last();
    string backup = $"{target}.before-cap-{season}";
    if (File.Exists(backup)) backup += "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
    File.Copy(target, backup);
    Console.WriteLine("Backup: " + backup);
    // UTF-8 without a BOM, two-space indent, like the emulator's own patch files.
    using (var w = System.Xml.XmlWriter.Create(target, new System.Xml.XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = true, IndentChars = "  " }))
        doc.Save(w);
    Console.WriteLine("Wrote: " + target);
}

var readBack = patch.ReadBack(XDocument.Load(target), eboot);
foreach (var p in readBack) Console.Error.WriteLine("READ-BACK: " + p);
Console.WriteLine(readBack.Count == 0 ? "Read-back OK: every line is on a stock site and writes the configured value." : "Read-back FAILED.");
return readBack.Count == 0 ? 0 : 1;
