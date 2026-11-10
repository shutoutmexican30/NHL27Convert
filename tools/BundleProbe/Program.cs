// BundleProbe <game> [resource name...]
// Read-only. Builds the bundle index (BundleIndex) and, for each name, prints its RES entries, the decoded RES size,
// and for textures the 132-byte header's chunk id and where that chunk is stored (TOC chunk table or bundle).
using System.Diagnostics;
using Suite.Core;
using Suite.Core.Frostbite;

var sw = Stopwatch.StartNew();
if (args.Length >= 3 && args[1] == "--bundles")
{
    // --bundles <game> <filter>: read-only bundle-name listing, no index build.
    foreach (var toc in BundleIndex.Tocs(args[0]))
    {
        TocArchive archive;
        try { archive = TocArchive.Parse(File.ReadAllBytes(toc)); }
        catch (Exception ex) { Console.WriteLine($"SKIP {Path.GetFileName(toc)}: {ex.Message}"); continue; }
        foreach (var b in archive.Bundles.Where(b => b.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"{Path.GetFileName(toc)}: {b.Name}");
    }
    return;
}
Oodle.Ensure(SuiteSettings.Load());
if (args.Length == 3 && args[1] == "--registry-audit")
{
    // --registry-audit <game> <mod.json>: read-only check of every source folder and its actual lookup destinations.
    var auditMod = ModFile.Load(args[2]) ?? throw new InvalidDataException("Could not load " + args[2]);
    var auditIndex = BundleIndex.Build(args[0], s => Console.WriteLine(s));
    string artDir = Path.Combine(Directory.GetCurrentDirectory(), "Debugging", "source-candidate-art");
    Directory.CreateDirectory(artDir);
    Rgba? BaseImage(string folder)
    {
        string id = KitRegistration.Identity(folder), prefix = folder.TrimEnd('/') + "/jersey_adidas_" + id;
        string name = auditIndex.ResByName.ContainsKey(prefix + "_base_color") ? prefix + "_base_color" : prefix + "_color";
        if (!auditIndex.ResByName.ContainsKey(name)) return null;
        try { return Ps4Codec.DecodeSlices(TextureSlots.Read(args[0], auditIndex, name).Res, TextureSlots.Read(args[0], auditIndex, name).Stock)[0]; }
        catch { return null; }
    }
    int ArraySlices(string folder)
    {
        string name = folder.TrimEnd('/') + "/branding_jersey_adidas_" + KitRegistration.Identity(folder) + "_color_array";
        var res = auditIndex.ResByName.GetValueOrDefault(name)?.FirstOrDefault();
        if (res is null) return 0;
        byte[] bytes = BundleIndex.ReadBytes(res.Data);
        try { bytes = ChunkBlocks.Parse(bytes).Decode(); } catch (InvalidDataException) { }
        return Ps4Codec.Slices(bytes);
    }
    static double Luma(Rgba image)
    {
        double sum = 0; int count = 0;
        for (int i = 0; i + 3 < image.Pixels.Length; i += 64)
            if (image.Pixels[i + 3] > 128)
            {
                sum += 0.2126 * image.Pixels[i] + 0.7152 * image.Pixels[i + 1] + 0.0722 * image.Pixels[i + 2];
                count++;
            }
        return count == 0 ? 0 : sum / count;
    }
    void Save(string team, string kit, string label, Rgba? image)
    {
        if (image is not null) ImageIO.SavePng(ImageIO.Shrink(image, 512), Path.Combine(artDir, $"{team}-{kit}-{label}.png"));
    }
    var missingSources = new HashSet<string>(StringComparer.Ordinal);
    foreach (var g in auditIndex.ResByName[PlayerTeamRegistry.ResourceName].GroupBy(r => (r.Data.Cas, r.Data.Offset)))
    {
        byte[] stored = BundleIndex.ReadBytes(g.First().Data), raw;
        try { raw = ChunkBlocks.Parse(stored).Decode(); } catch (InvalidDataException) { raw = stored; }
        Console.WriteLine($"REGISTRY {Path.GetRelativePath(args[0], g.Key.Cas)} @{g.Key.Offset}");
        foreach (var a in auditMod.Additions ?? [])
        {
            int direct = PlayerTeamRegistry.Rows(raw, a.SourcePrefix.TrimEnd('/')).Count;
            var resolved = a.Textures.Select(t => PlayerTeamRegistry.Resolve(raw, t.SourceSlot)).Where(r => r is not null).ToArray();
            string destinations = string.Join("; ", resolved.GroupBy(r => r!.Folder)
                .Select(group => $"{group.Key} ({group.Count()} textures)"));
            Console.WriteLine($"{a.Team} {a.SourceKit}: direct={direct}; resolved={resolved.Length}/{a.Textures.Count}; {destinations}");
            if (direct != 0 || !missingSources.Add(a.Team + " " + a.SourceKit)) continue;
            string source = a.SourcePrefix.TrimEnd('/'), parent = source[..source.LastIndexOf('/')];
            string fromId = KitRegistration.Identity(source);
            var sourceBase = BaseImage(source);
            Save(a.Team, a.SourceKit, "original", sourceBase);
            Console.WriteLine($"  SOURCE ART arraySlices={ArraySlices(source)}; luma={(sourceBase is null ? "missing" : Luma(sourceBase).ToString("F1"))}");
            foreach (string folder in PlayerTeamRegistry.Folders(raw, parent))
            {
                string toId = KitRegistration.Identity(folder);
                var mapped = a.Textures.Select(t => folder + "/" + t.SourceSlot[(source.Length + 1)..].Replace(fromId, toId, StringComparison.Ordinal)).ToArray();
                int present = mapped.Count(auditIndex.ResByName.ContainsKey);
                var rows = PlayerTeamRegistry.Rows(raw, folder);
                int registered = mapped.Count(name => rows.Any(r => name.EndsWith("/" + r.Leaf, StringComparison.Ordinal)));
                var candidateBase = BaseImage(folder);
                bool same = sourceBase is not null && candidateBase is not null && sourceBase.Width == candidateBase.Width
                    && sourceBase.Height == candidateBase.Height && sourceBase.Pixels.AsSpan().SequenceEqual(candidateBase.Pixels);
                Save(a.Team, a.SourceKit, folder.Split('/')[^1], candidateBase);
                Console.WriteLine($"  CANDIDATE {folder}: rows={rows.Count}; resources={present}/{mapped.Length}; registeredTextures={registered}; "
                    + $"arraySlices={ArraySlices(folder)}; luma={(candidateBase is null ? "missing" : Luma(candidateBase).ToString("F1"))}; identicalBase={same}");
            }
        }
    }
    return;
}
if (args.Length > 3 && args[1] == "--registry")
{
    // --registry <game> <source-kit-folder> <target-kit-folder>: inspect the game's name-to-bundle lookup, read-only.
    var registryIndex = BundleIndex.Build(args[0], s => Console.WriteLine(s));
    foreach (var g in registryIndex.ResByName[PlayerTeamRegistry.ResourceName].GroupBy(r => (r.Data.Cas, r.Data.Offset)))
    {
        var r = g.First();
        byte[] stored = BundleIndex.ReadBytes(r.Data);
        byte[] raw;
        try { raw = ChunkBlocks.Parse(stored).Decode(); } catch (InvalidDataException) { raw = stored; }
        Console.WriteLine($"REGISTRY {Path.GetRelativePath(args[0], r.Data.Cas)} @{r.Data.Offset}: {raw.Length} bytes; {g.Count()} copies; sorted={PlayerTeamRegistry.IsSorted(raw)}");
        foreach (string folder in args.Skip(2))
        {
            var rows = PlayerTeamRegistry.Rows(raw, folder);
            Console.WriteLine($"FOLDER {folder}: {rows.Count} assets, {rows.Select(x => x.Bundle).Distinct().Count()} bundles");
            foreach (var row in rows) Console.WriteLine($"  {row.Leaf} -> {PlayerTeamRegistry.BundleName(row.Bundle)}");
        }
    }
    return;
}
if (args.Length == 4 && args[1] == "--registry-probe")
{
    // --registry-probe <game> <source-kit-folder> <target-kit-folder>: build and verify new lookup rows in memory only.
    var probeIndex = BundleIndex.Build(args[0], s => Console.WriteLine(s));
    foreach (var g in probeIndex.ResByName[PlayerTeamRegistry.ResourceName].GroupBy(r => (r.Data.Cas, r.Data.Offset)))
    {
        var r = g.First();
        byte[] stored = BundleIndex.ReadBytes(r.Data), raw;
        try { raw = ChunkBlocks.Parse(stored).Decode(); } catch (InvalidDataException) { raw = stored; }
        var result = PlayerTeamRegistry.Register(raw, r.Meta, [new KitRegistration(args[2], args[3])]);
        var source = PlayerTeamRegistry.Rows(result.Raw, args[2]);
        var target = PlayerTeamRegistry.Rows(result.Raw, args[3]);
        if (source.Count != target.Count || target.Count == 0 || !PlayerTeamRegistry.IsSorted(result.Raw))
            throw new InvalidDataException("Registration read-back has the wrong asset count or sort order.");
        foreach (var row in target)
            if (!PlayerTeamRegistry.Contains(result.Raw, row.Leaf)
                || !PlayerTeamRegistry.Contains(result.Raw, row.Folder + "/" + row.Leaf)
                || PlayerTeamRegistry.BundleName(row.Bundle).Contains("/home/", StringComparison.Ordinal))
                throw new InvalidDataException("Registration contains an unresolved or source-kit target: " + row.Leaf);
        Console.WriteLine($"REGISTRY PROBE {Path.GetRelativePath(args[0], r.Data.Cas)}: {target.Count} assets, "
            + $"{target.Select(x => x.Bundle).Distinct().Count()} bundles; all lookup rows verified in memory; no files written.");
    }
    foreach (string folder in new[] { args[2], args[3] })
    {
        var resources = probeIndex.ResByName.Where(kv => kv.Key.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal)).ToList();
        int bad = 0;
        foreach (var (name, copies) in resources)
        {
            byte[] raw = ChunkBlocks.Parse(BundleIndex.ReadBytes(copies[0].Data)).Decode();
            uint actual = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(112));
            uint hash = 5381; foreach (char c in name) hash = (hash * 33) ^ c;
            if (actual != hash) { bad++; Console.WriteLine($"NAME HASH MISMATCH {name}: stored=0x{actual:x8} expected=0x{hash:x8}"); }
        }
        Console.WriteLine($"RESOURCE NAME HASHES {folder}: {resources.Count} resources; {bad} mismatches.");
    }
    return;
}
var index = BundleIndex.Build(args[0], s => Console.WriteLine(s));
Console.WriteLine($"index built in {sw.Elapsed.TotalSeconds:F1}s");
var tocs = BundleIndex.Tocs(args[0]).Select(TocFile.Load).ToList();
if (args.Length > 3 && args[1] == "--layout")
{
    // --layout <mod.json> <team>: compare each target texture's actual bundle with its source team's mapped layout.
    var mod = ModFile.Load(args[2]) ?? throw new InvalidDataException("Could not load " + args[2]);
    var additions = (mod.Additions ?? []).Where(a => a.Team.Equals(args[3], StringComparison.OrdinalIgnoreCase)).ToList();
    if (additions.Count == 0) throw new InvalidDataException("No additions for " + args[3]);
    var bundleNames = new Dictionary<(string Toc, int RecordAt), string>();
    string BundleName(BundleIndex.Bundle bundle)
    {
        var key = (bundle.Toc, bundle.RecordAt);
        if (bundleNames.TryGetValue(key, out var name)) return name;
        var archive = TocArchive.Parse(File.ReadAllBytes(bundle.Toc));
        name = archive.Bundles.Single(b => b.RecordOffset == (ulong)(bundle.RecordAt - TocFile.HeaderSize)).Name;
        bundleNames.Add(key, name);
        return name;
    }
    static string Identity(string prefix) => prefix.Trim('/').Split('/') is { } p ? p[^3] + "_" + p[^1] : "";
    string MapBundle(string name, string sourcePrefix, string targetPrefix)
    {
        bool gen4b = name.StartsWith("gen4b/", StringComparison.Ordinal);
        string value = gen4b ? name[6..] : name;
        if (!value.StartsWith(sourcePrefix, StringComparison.Ordinal)) return "<outside source: " + name + ">";
        string mapped = targetPrefix + value[sourcePrefix.Length..]
            .Replace(Identity(sourcePrefix), Identity(targetPrefix), StringComparison.Ordinal);
        return gen4b ? "gen4b/" + mapped : mapped;
    }
    int mismatches = 0;
    foreach (var addition in additions)
    {
        Console.WriteLine($"{addition.Team} {addition.SourceKit}: variant {addition.TargetVariant}");
        foreach (var texture in addition.Textures)
        {
            var sources = index.ResByName.GetValueOrDefault(texture.SourceSlot) ?? [];
            var targets = index.ResByName.GetValueOrDefault(texture.TargetSlot) ?? [];
            var expected = sources.Select(s => MapBundle(BundleName(s.Bundle), addition.SourcePrefix, addition.TargetPrefix))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var actual = targets.Select(t => BundleName(t.Bundle)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var missing = expected.Except(actual, StringComparer.OrdinalIgnoreCase).ToArray();
            if (missing.Length == 0 && actual.Length > 0) continue;
            mismatches++;
            Console.WriteLine($"MISMATCH {texture.TargetSlot}");
            Console.WriteLine("  expected: " + string.Join(" | ", expected));
            Console.WriteLine("  actual:   " + (actual.Length == 0 ? "<not found>" : string.Join(" | ", actual)));
        }
    }
    Console.WriteLine($"LAYOUT MISMATCHES: {mismatches}");
    return;
}
if (args.Length > 3 && args[1] == "--dependencies")
{
    // --dependencies <mod.json> <team>: find target EBX imports that still resolve to source/donor team assets.
    var mod = ModFile.Load(args[2]) ?? throw new InvalidDataException("Could not load " + args[2]);
    var additions = (mod.Additions ?? []).Where(a => a.Team.Equals(args[3], StringComparison.OrdinalIgnoreCase)).ToList();
    if (additions.Count == 0) throw new InvalidDataException("No additions for " + args[3]);
    EbxRiff ReadEbx(BundleIndex.Ebx asset)
    {
        byte[] stored = BundleIndex.ReadBytes(asset.Data);
        byte[] raw;
        try { raw = ChunkBlocks.Parse(stored).Decode(); } catch (InvalidDataException) { raw = stored; }
        return EbxRiff.Parse(raw);
    }
    var targetNames = additions.SelectMany(a => index.EbxByName.Keys.Where(n => n.StartsWith(a.TargetPrefix, StringComparison.Ordinal)))
        .Distinct(StringComparer.Ordinal).ToArray();
    var targetParsed = targetNames.ToDictionary(name => name, name => ReadEbx(index.EbxByName[name][0]), StringComparer.Ordinal);
    var targetGuids = targetParsed.ToDictionary(kv => kv.Value.FileGuid, kv => kv.Key);
    var needed = targetParsed.Values.SelectMany(e => e.ImportedFileGuids).Where(id => !targetGuids.ContainsKey(id)).ToHashSet();
    var resolved = new Dictionary<Guid, string>();
    var candidates = index.EbxByName.SelectMany(kv => kv.Value.Take(1))
        .Where(e => e.Name.StartsWith(TextureName.SkaterPrefix + "nhl/", StringComparison.Ordinal)).ToArray();
    Console.WriteLine($"Scanning {candidates.Length:N0} NHL EBX assets for {needed.Count} external target import(s)...");
    Parallel.ForEach(candidates, asset =>
    {
        EbxRiff parsed;
        try { parsed = ReadEbx(asset); } catch { return; }
        if (!needed.Contains(parsed.FileGuid)) return;
        lock (resolved) resolved[parsed.FileGuid] = asset.Name;
    });
    int external = 0;
    foreach (var (name, parsed) in targetParsed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
    foreach (var import in parsed.ImportedFileGuids)
    {
        if (targetGuids.ContainsKey(import) || !resolved.TryGetValue(import, out var donor)) continue;
        external++;
        Console.WriteLine($"EXTERNAL {name}\n  -> {donor}");
    }
    Console.WriteLine($"EXTERNAL TEAM DEPENDENCIES: {external}; unresolved non-team/global imports: {needed.Count - resolved.Count}");
    return;
}
if (args.Length > 3 && args[1] == "--dump")
{
    // --dump <out-dir> <name>...: decoded EBX/RES bytes (first copy) for inspection.
    Directory.CreateDirectory(args[2]);
    foreach (var a in args.Skip(3))
    {
        // "res:<name>" picks the RES when an EBX has the same name (textures).
        bool wantRes = a.StartsWith("res:"); var n = wantRes ? a[4..] : a;
        var loc = (wantRes ? null : index.EbxByName.GetValueOrDefault(n)?[0].Data) ?? index.ResByName.GetValueOrDefault(n)?[0].Data;
        if (loc is null) { Console.WriteLine($"{n}: not found"); continue; }
        var st = BundleIndex.ReadBytes(loc);
        byte[] dec; try { dec = ChunkBlocks.Parse(st).Decode(); } catch { dec = st; }
        var file = Path.Combine(args[2], n.Replace('/', '~') + (!wantRes && index.EbxByName.ContainsKey(n) ? ".ebx" : ".res"));
        File.WriteAllBytes(file, dec);
        Console.WriteLine($"{n}: {dec.Length} bytes -> {file}");
        if ((wantRes || !index.EbxByName.ContainsKey(n)) && dec.Length >= 48)
        {
            // A texture: also its full chunk, from the TOC chunk table (else the bundle copy that holds all of it).
            var id = new Guid(dec.AsSpan(32, 16));
            var hit = tocs.Select(t => t.Find(id)).FirstOrDefault(x => x is not null);
            byte[]? raw = null;
            if (hit is not null) raw = ChunkBlocks.Parse(BundleIndex.ReadBytes(new BundleIndex.Location(hit.Toc, 0, BundleIndex.CasPath(args[0], hit.Patch, hit.Catalog, hit.Cas), hit.Offset, hit.Size))).Decode();
            else if (index.ChunksById.GetValueOrDefault(id)?.FirstOrDefault(c => c.LogicalOffset == 0) is { } bc) raw = ChunkBlocks.Parse(BundleIndex.ReadBytes(bc.Data)).Decode();
            if (raw is not null) { File.WriteAllBytes(Path.ChangeExtension(file, ".chunk"), raw); Console.WriteLine($"   chunk {id}: {raw.Length} bytes"); }
        }
    }
    return;
}
if (args.Length > 2 && args[1] == "--scan")
{
    // --scan <name filter or *> <int|float values...>: decoded EBX/RES whose bytes hold any of the values (LE int32/float32).
    var pats = args.Skip(3).SelectMany(v => new[] { BitConverter.GetBytes(int.Parse(v)), BitConverter.GetBytes((float)int.Parse(v)) }).ToList();
    var items = index.EbxByName.Values.Select(l => (l[0].Name, l[0].Data, Kind: "EBX"))
        .Concat(index.ResByName.Values.Select(l => (l[0].Name, l[0].Data, Kind: "RES")))
        .Where(x => args[2] == "*" || x.Name.Contains(args[2], StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine($"scanning {items.Count}");
    Parallel.ForEach(items, x =>
    {
        try
        {
            if (x.Data.Size > 8 << 20) return;
            var st = BundleIndex.ReadBytes(x.Data);
            byte[] dec; try { dec = ChunkBlocks.Parse(st).Decode(); } catch { dec = st; }
            foreach (var p in pats)
                if (dec.AsSpan().IndexOf(p) is var at and >= 0) { lock (pats) Console.WriteLine($"{x.Kind} {x.Name} @{at} {Convert.ToHexString(p)}"); }
        }
        catch { }
    });
    return;
}
if (args.Length > 2 && args[1] == "--grep")
{
    foreach (var (n, l) in index.ResByName.Where(kv => kv.Key.Contains(args[2], StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key))
        Console.WriteLine($"{n}	0x{l[0].Type:X8}	{l.Count}");
    foreach (var (n, l) in index.EbxByName.Where(kv => kv.Key.Contains(args[2], StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key))
        Console.WriteLine($"EBX {n}	{l.Count}	{Path.GetRelativePath(args[0], l[0].Data.Cas)} @{l[0].Data.Offset} {l[0].Data.Size}");
    return;
}
foreach (var name in args.Skip(1).SelectMany(a => index.ResByName.ContainsKey(a) ? [a]
    : index.ResByName.Keys.Where(k => k.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty(a)))
{
    if (!index.ResByName.TryGetValue(name, out var list)) { Console.WriteLine($"{name}: no RES"); continue; }
    foreach (var r in list)
    {
        var stored = BundleIndex.ReadBytes(r.Data);
        byte[] raw;
        try { raw = ChunkBlocks.Parse(stored).Decode(); } catch { raw = stored; }
        Console.WriteLine($"{name}: type 0x{r.Type:X8} in {Path.GetRelativePath(args[0], r.Bundle.Toc)} bundle '{r.Bundle.FirstName}', stored {stored.Length}, raw {raw.Length}");
        if (raw.Length >= 48)
        {
            var id = new Guid(raw.AsSpan(32, 16));
            int w = BitConverter.ToUInt16(raw, 22), h = BitConverter.ToUInt16(raw, 24), mips = raw[30];
            Console.WriteLine($"   header: {w}x{h}, {mips} mips, chunk {id}");
            foreach (var tocHit in tocs.Select(t => t.Find(id)).Where(x => x is not null))
                Console.WriteLine($"   chunk in TOC table {Path.GetRelativePath(args[0], tocHit.Toc)}: patch={tocHit.Patch} cat={tocHit.Catalog} cas={tocHit.Cas} @{tocHit.Offset} {tocHit.Size}");
            if (index.ChunksById.TryGetValue(id, out var bc))
                foreach (var c in bc) Console.WriteLine($"   chunk in bundle '{c.Bundle.FirstName}' ({Path.GetRelativePath(args[0], c.Bundle.Toc)}): {Path.GetRelativePath(args[0], c.Data.Cas)} @{c.Data.Offset} {c.Data.Size}, logical {c.LogicalOffset}+{c.LogicalSize}");
        }
    }
}
