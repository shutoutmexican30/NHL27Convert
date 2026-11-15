// SpeechProbe <game> [--summary] [--verify] [--all-waves] [--phrases] [--export <dir>] <query>...
// Read only. <game> is an NHL 24 game folder (PS4 build) or an NHL 27 PS5 app0 folder (has Data\Ps5).
// Loads the main-commentary speech index (transcriptions, keywords, surname waves' Harmony banks) and, for each
// query, prints every take: wave, VariationId, keywords, offset/length in the wave chunk, duration, text.
// A query is a surname ("Stamkos"), "kw:1091" (surname keyword) or "var:87275" (VariationId).
//   --summary    per wave: takes, takes with keywords, distinct keywords, chunk size.
//   --verify     read each wave chunk and check every take's offset is an SPS stream of the listed length
//                (the bank's duration column is a playback length, not the stream length, so it is not compared).
//   --all-waves  index every main-commentary wave, not just *surname* ones (slow).
//   --phrases    a surname query also matches phrase takes ("to Stamkos", "by Stamkos").
//   --export     write each matching take as <dir>/<query>/<variation>_<folder>_<wave>.sps (raw EA SPS Opus).
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Sounds;

if (args.Length < 1) { Console.Error.WriteLine("usage: SpeechProbe <game> [--summary] [--verify] [--all-waves] [--phrases] [--export <dir>] <query>..."); return 2; }
string game = args[0];
bool all = args.Contains("--all-waves"), verify = args.Contains("--verify"), phrases = args.Contains("--phrases");
int ei = Array.IndexOf(args, "--export");
string? export = ei > 0 ? args[ei + 1] : null;
var queries = args.Skip(1).Where((a, i) => !a.StartsWith("--") && !(ei > 0 && i + 1 == ei + 1)).ToList();

ISoundAssets assets;
if (Directory.Exists(Path.Combine(game, "Data", "Ps5")))
{
    var ps5 = Ps5SoundAssets.Build(game);
    Console.WriteLine($"PS5: {ps5.Bundles:N0} bundles, {ps5.EbxNames.Count():N0} EBX, {ps5.ChunkLocations.Count:N0} chunks" + (ps5.Warnings.Count > 0 ? $", {ps5.Warnings.Count} warnings" : ""));
    assets = ps5;
}
else
{
    Oodle.Ensure(SuiteSettings.Load());
    assets = new Nhl24SoundAssets(BundleIndex.Build(game));
}
var cat = SpeechCatalog.Load(assets, n => all || n.Contains("surname", StringComparison.OrdinalIgnoreCase));
Console.WriteLine($"{cat.Waves.Count} waves, {cat.Clips.Count} takes, {cat.Transcriptions.Count} transcriptions, {cat.Keywords.Count} keyword entries");
string Short(string wave) => wave[(SpeechCatalog.Root.Length + "soundwaves/".Length)..];

if (args.Contains("--summary"))
    foreach (var w in cat.Waves)
    {
        var cs = cat.Clips.Where(c => c.Wave == w.Name).ToList();
        Console.WriteLine($"   {Short(w.Name),-48} {cs.Count,6} takes {cs.Count(c => c.Keywords.Length > 0),6} keyed {cs.SelectMany(c => c.Keywords).Distinct().Count(),5} keywords {w.ChunkSize / 1e6,7:0.0} MB");
    }

var chunkCache = new Dictionary<Guid, byte[]>();
byte[] Chunk(SpeechCatalog.Wave w) => chunkCache.TryGetValue(w.ChunkId, out var c) ? c : chunkCache[w.ChunkId] = cat.ChunkOf(w);

if (verify)
{
    int bad = 0;
    foreach (var w in cat.Waves)
    {
        var raw = Chunk(w);
        var streams = SpsStream.ReadAll(raw).ToDictionary(s => s.Offset);
        var takes = cat.Clips.Where(c => c.Wave == w.Name).ToList();
        foreach (var c in takes)
            if (!streams.TryGetValue(c.Offset, out var s) || s.Length > c.Length || c.Length - s.Length > 3)
            { bad++; if (bad < 20) Console.WriteLine($"MISMATCH {w.Name} var {c.VariationId} @{c.Offset}: stream {(s is null ? "none" : $"{s.Length} bytes")}, take {c.Length}"); }
        if (raw.Length != w.ChunkSize || streams.Count != takes.Count)
        { bad++; Console.WriteLine($"MISMATCH {w.Name}: chunk {raw.Length}/{w.ChunkSize} bytes, {streams.Count} streams vs {takes.Count} takes"); }
    }
    Console.WriteLine(bad == 0 ? "verify: every take offset/length matches an SPS stream in its wave chunk" : $"verify: {bad} mismatches");
}

foreach (var q in queries)
{
    var hits = q.StartsWith("kw:") ? cat.ByKeyword(int.Parse(q[3..]))
        : q.StartsWith("var:") ? cat.Clips.Where(c => c.VariationId == uint.Parse(q[4..]))
        : cat.ByText(q, phrases);
    var list = hits.ToList();
    Console.WriteLine($"== {q}: {list.Count} take(s), keywords {string.Join(",", list.SelectMany(c => c.Keywords).Distinct())}");
    foreach (var c in list)
    {
        Console.WriteLine($"   {Short(c.Wave),-48} var {c.VariationId,8} kw {string.Join(",", c.Keywords),-10} @{c.Offset,9} len {c.Length,6} {c.Seconds:0.000}s  \"{c.Text}\"");
        if (export is null) continue;
        var w = cat.Waves.First(x => x.Name == c.Wave);
        var raw = Chunk(w);
        var s = SpsStream.TryRead(raw, c.Offset) ?? throw new InvalidDataException($"no SPS stream at {c.Offset} in {w.Name}");
        var dir = Path.Combine(export, string.Concat(q.Split(Path.GetInvalidFileNameChars())));
        Directory.CreateDirectory(dir);
        var parts = c.Wave.Split('/');
        File.WriteAllBytes(Path.Combine(dir, $"{c.VariationId}_{parts[^2]}_{parts[^1]}.sps"), raw.AsSpan(c.Offset, s.Length).ToArray());
    }
}
return 0;
