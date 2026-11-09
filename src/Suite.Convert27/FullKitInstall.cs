using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

namespace Suite.Convert27;

/// <summary>
/// Installs new uniforms under the one-ID rule: each new kit is a whole clone of the same team's source kit (every bundle,
/// in the same TOC layers as the source, with its own GUIDs, resource ids, name hashes and chunks). The converted artwork
/// replaces the mapped textures, every name is registered in both versions of player_team_brt, and the jersey and
/// equipment rows use the same variant. The target kits must not exist yet.
/// </summary>
public sealed class FullKitInstall
{
    public sealed record Options(string Target, ModFile Mod, string ArtDir, string? BackupRoot, bool Apply, Action<string> Say, int Workers = 0);

    readonly Options o;
    readonly SuiteSettings settings;
    string Game => o.Target;
    BundleIndex index = null!;
    Dictionary<string, byte[]> tocFiles = null!;
    Dictionary<string, TocArchive> archives = null!;
    Dictionary<(string, ulong), TocArchive.Bundle> bundleNames = null!;
    HashSet<ulong> usedRids = null!;
    Lazy<Dictionary<Guid, List<BundleIndex.Location>>> globalChunks = null!;

    readonly Dictionary<string, List<TocArchive.NewBundle>> newBundles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<Guid, TocArchive.Chunk>> newChunks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> appended = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Guid, byte[]> artByChunk = [];
    readonly Dictionary<string, HashSet<string>> artHashes = new(StringComparer.Ordinal);
    int bundleCount, ebxCount, resCount;

    FullKitInstall(Options options, SuiteSettings suiteSettings) { o = options; settings = suiteSettings; }

    public static int Run(Options options, SuiteSettings settings) => new FullKitInstall(options, settings).Execute();

    static byte[] Dec(byte[] stored) { try { return ChunkBlocks.Parse(stored).Decode(); } catch (InvalidDataException) { return stored; } }
    static uint Djb(string s) { uint h = 5381; foreach (char c in s) h = (h * 33) ^ c; return h; }
    static Guid Stable(string id)
    {
        var b = SHA1.HashData(Encoding.ASCII.GetBytes("NHL27Convert:kit:" + id)).AsSpan(0, 16).ToArray();
        b[7] = (byte)((b[7] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b);
    }
    static ulong StableRid(string name) => BinaryPrimitives.ReadUInt64LittleEndian(SHA1.HashData(Encoding.ASCII.GetBytes("NHL27Convert:rid:" + name)));
    static byte[] EncodeLike(byte[] old, byte[] raw)
    {
        ChunkBlocks parsed;
        try { parsed = ChunkBlocks.Parse(old); } catch (InvalidDataException) { return raw; }
        if (parsed.Blocks.Sum(b => b.RawSize) != raw.Length) return ChunkBlocks.EncodeBlocks(raw);
        using var ms = new MemoryStream();
        int at = 0;
        foreach (var b in parsed.Blocks) { ms.Write(ChunkBlocks.EncodeBlock(raw.AsSpan(at, b.RawSize))); at += b.RawSize; }
        return ms.ToArray();
    }
    static void Pair(byte[] record, int at, uint offset, uint size)
    {
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(at, 4), offset);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(at + 4, 4), size);
    }
    uint LocationWord(string cas)
    {
        bool patch = cas.StartsWith(GameLayout.PatchDir(Game) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var parts = Path.GetRelativePath(patch ? GameLayout.PatchDir(Game) : GameLayout.DataDir(Game), cas).Replace('\\', '/').Split('/');
        if (parts.Length != 4) throw new InvalidDataException("Unexpected CAS path: " + cas);
        return (uint)((patch ? 1 << 16 : 0) | (int.Parse(parts[2][^2..]) << 8) | int.Parse(parts[3][4..6]));
    }
    string ResolveArt(string path)
    {
        if (path.Length == 0 || File.Exists(path)) return path;
        string local = Path.Combine(o.ArtDir, Path.GetFileName(path));
        return File.Exists(local) ? local : throw new FileNotFoundException("Converted artwork is missing.", path);
    }

    int Execute()
    {
        Oodle.Ensure(settings);
        if (o.Apply && ApplyTarget.RunningGame() is { } running) { o.Say("REFUSED: " + running + " is running."); return 1; }
        var legacy = new LegacyFiles(Game, settings);
        string metaPath = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
        File.WriteAllBytes(metaPath, legacy.Read("gamemodes/database/t3db/nhl_game-meta.xml"));
        var sources = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["nhl_game"] = legacy.Read(RosterSyncRun.LegacyName("nhl_game")),
            ["initial_experience"] = legacy.Read(RosterSyncRun.LegacyName("initial_experience")),
        };
        index = BundleIndex.Build(Game, o.Say);
        bool Exists(string name) => index.ResByName.ContainsKey(name) || index.EbxByName.ContainsKey(name);
        var mods = o.Mod.Additions ?? throw new InvalidDataException("The mod has no uniforms.");
        foreach (var a in mods)
        {
            string target = a.TargetPrefix.TrimEnd('/') + "/";
            if (index.EbxByName.Keys.Concat(index.ResByName.Keys).Any(n => n.StartsWith(target, StringComparison.Ordinal)))
            {
                o.Say($"REFUSED: {target} already exists. Restore a clean backup first.");
                return 1;
            }
        }
        var additionPlan = NewUniformAdditionPlanner.Build(mods, sources, metaPath, Exists);
        var databases = AddEquipment(additionPlan, metaPath);

        tocFiles = index.Bundles.Select(b => b.Toc).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(t => t, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        archives = tocFiles.ToDictionary(kv => kv.Key, kv => TocArchive.Parse(kv.Value), StringComparer.OrdinalIgnoreCase);
        bundleNames = [];
        foreach (var (toc, archive) in archives) foreach (var b in archive.Bundles) bundleNames[(toc, b.RecordOffset)] = b;
        usedRids = index.ResByName.Values.SelectMany(v => v).Select(r => r.Rid).ToHashSet();
        globalChunks = new(LoadGlobalChunks);

        var kits = additionPlan.Additions.Select(a => new KitRegistration(a.SourcePrefix.TrimEnd('/'), a.TargetPrefix.TrimEnd('/'))).ToList();
        if (!o.Apply)
        {
            foreach (var a in additionPlan.Additions) { PrepareArt(a); CloneKit(a, null); artByChunk.Clear(); o.Say($"planned {a.Team} {a.SourceKit} -> variant {a.TargetVariant}"); }
            ReportRegistry(kits);
            o.Say($"DRY RUN: {additionPlan.Additions.Count} kits, {bundleCount} bundles, {ebxCount} EBX, {resCount} RES. Nothing was written.");
            return 0;
        }

        string backup = ApplyTarget.Backup(Game, o.BackupRoot);
        o.Say("backup: " + backup);
        NewUniformTransaction.Run(backup,
            writeAssets: () =>
            {
                foreach (var a in additionPlan.Additions)
                {
                    o.Say($"writing {a.Team} {a.SourceKit} -> variant {a.TargetVariant}");
                    PrepareArt(a);
                    CloneKit(a, new Dictionary<(string, string), (uint, uint)>());
                    artByChunk.Clear();
                }
                RebuildTocs();
                Register(kits);
            },
            writeDatabases: () =>
            {
                foreach (string line in new LegacyFiles(Game, settings).Replace(databases)) o.Say("DB: " + line);
            },
            readBack: () => ReadBack(additionPlan.Additions, kits, databases),
            restoreAndVerify: () =>
            {
                ApplyTarget.RestoreFromBackup(Game, backup);
                var left = ApplyTarget.Compare(Game, backup);
                if (left.Count > 0) throw new InvalidOperationException("Rollback differences: " + string.Join("; ", left.Take(10)));
            });
        o.Say($"INSTALLED AND READ BACK: {additionPlan.Additions.Count} kits, {bundleCount} bundles, {ebxCount} EBX, {resCount} RES. Backup: {backup}");
        return 0;
    }

    /// <summary>Variant-specific equipment rows of the source kit, repeated for the new variant, over the planned jersey rows.</summary>
    Dictionary<string, byte[]> AddEquipment(NewUniformAdditionPlan plan, string metaPath)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in plan.JerseyRows.ChangedDatabases)
        {
            var bytes = plan.JerseyRows.Databases[name];
            var db = T3Db.Load((byte[])bytes.Clone(), metaPath);
            var t = db.Tables["stockteamequipmenttable"];
            int added = 0;
            foreach (var a in plan.Additions)
            {
                int sourceVariant = a.SourceKit == "away" ? 1 : 0;
                var rows = Enumerable.Range(0, t.InUse).Where(r => db.GetInt(t, r, "team") == a.TeamId && db.GetInt(t, r, "variant") == sourceVariant).ToList();
                if (t.InUse + rows.Count > t.Slots) throw new InvalidDataException($"{name}: no spare equipment rows for {a.Team}.");
                foreach (int r in Enumerable.Reverse(rows))
                {
                    t = db.InsertRecord(t, r + 1, r);
                    db.SetInt(t, r + 1, "variant", a.TargetVariant);
                    added++;
                }
            }
            if (added > 0) for (int r = 0; r < t.InUse; r++) db.SetInt(t, r, "index", r);
            var saved = db.Save();
            if (saved.Length != bytes.Length) throw new InvalidDataException($"{name}: database length changed.");
            T3Db.Load((byte[])saved.Clone(), metaPath);
            files[RosterSyncRun.LegacyName(name)] = saved;
            o.Say($"{name}: {added} equipment row(s) added");
        }
        return files;
    }

    /// <summary>Encodes every mapped texture into its same-team source slot layout; unused array layers are blanked.</summary>
    void PrepareArt(NewUniformAddition a)
    {
        foreach (var texture in a.Textures)
        {
            string source = texture.SourceSlot.Trim().Replace('\\', '/').TrimStart('/');
            string target = texture.TargetSlot.Trim().Replace('\\', '/').TrimStart('/');
            var art = texture.Artwork;
            var resolved = art with
            {
                Slot = source,
                Source = ResolveArt(art.Source),
                Layers = art.Layers?.ToDictionary(kv => kv.Key, kv => ResolveArt(kv.Value)),
            };
            if (resolved.Source.Length == 0 && resolved.Layers is not { Count: > 0 }) continue;
            // An update can carry the same texture again with a different layout, and the game may read either copy.
            var slots = TextureSlots.ReadAll(Game, index, source);
            int need = resolved.Layers is { Count: > 0 } ? resolved.Layers.Keys.Max() + 1 : 1;
            if (slots.All(sl => Ps4Codec.Slices(sl.Res.Header) < need)) throw new InvalidDataException(
                $"{a.Team} {a.SourceKit}: {source} has {slots.Max(sl => Ps4Codec.Slices(sl.Res.Header))} layer(s), the artwork needs {need}. This kit needs a deeper array first.");
            string lower = target.ToLowerInvariant();
            bool blankable = lower.EndsWith("_color_array", StringComparison.Ordinal) || lower.EndsWith("_normal_array", StringComparison.Ordinal);
            var hashes = artHashes[target] = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slot in slots)
            {
                int have = Ps4Codec.Slices(slot.Res.Header);
                var copy = resolved;
                if (have < need)
                {
                    copy = resolved with { Layers = resolved.Layers!.Where(kv => kv.Key < have).ToDictionary(kv => kv.Key, kv => kv.Value) };
                    o.Say($"note: {target}: one copy has {have} layer(s), artwork layer(s) {have}-{need - 1} do not fit that copy");
                }
                byte[] raw = TextureSlots.Build(slot, copy, o.Workers);
                if (blankable && copy.Layers is { Count: > 0 })
                {
                    bool normal = lower.EndsWith("_normal_array", StringComparison.Ordinal);
                    var pixels = new byte[slot.Res.Width * slot.Res.Height * 4];
                    if (normal) for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 128; pixels[i + 1] = 128; pixels[i + 2] = 255; }
                    var blank = new Rgba(slot.Res.Width, slot.Res.Height, pixels);
                    var unused = Enumerable.Range(0, have).Where(l => !copy.Layers.ContainsKey(l)).ToDictionary(l => l, _ => blank);
                    if (unused.Count > 0) raw = Ps4Codec.Build(slot.Res, raw, unused, o.Workers);
                }
                artByChunk[slot.Chunk] = raw;
                hashes.Add(Convert.ToHexString(SHA256.HashData(raw)));
            }
        }
    }

    Dictionary<Guid, List<BundleIndex.Location>> LoadGlobalChunks()
    {
        var map = new Dictionary<Guid, List<BundleIndex.Location>>();
        foreach (var path in BundleIndex.Tocs(Game))
        {
            var toc = TocFile.Load(path);
            foreach (var id in toc.Chunks)
            {
                var c = toc.Find(id)!;
                if (!map.TryGetValue(id, out var list)) map[id] = list = [];
                list.Add(new BundleIndex.Location(path, 0, BundleIndex.CasPath(Game, c.Patch, c.Catalog, c.Cas), c.Offset, c.Size));
            }
        }
        return map;
    }

    byte[] FullChunk(Guid id, long size)
    {
        var inBundles = index.ChunksById.GetValueOrDefault(id)?.Where(x => x.LogicalOffset == 0 && x.LogicalSize == size).Select(x => x.Data) ?? [];
        foreach (var location in inBundles.Concat(globalChunks.Value.GetValueOrDefault(id) ?? []))
        {
            if (!File.Exists(location.Cas)) continue;
            var stored = BundleIndex.ReadBytes(location);
            if (Dec(stored).Length == size) return stored;
        }
        throw new InvalidDataException("No full chunk found for " + id);
    }

    TocArchive.Bundle NameOf(BundleIndex.Bundle b) => bundleNames[(b.Toc, (ulong)(b.RecordAt - TocFile.HeaderSize))];

    /// <summary>Plans one whole kit; with <paramref name="where"/> set, appends its payloads and manifests to the CAS files.</summary>
    void CloneKit(NewUniformAddition a, Dictionary<(string Cas, string Key), (uint Offset, uint Size)>? where)
    {
        string pre = a.SourcePrefix.TrimEnd('/') + "/", tgt = a.TargetPrefix.TrimEnd('/') + "/";
        string idFrom = KitRegistration.Identity(pre), idTo = KitRegistration.Identity(tgt);
        string fromWord = pre.TrimEnd('/').Split('/')[^1];
        string Map(string n) => tgt + n[pre.Length..].Replace(idFrom, idTo, StringComparison.Ordinal);

        var kitEbx = index.EbxByName.Where(k => k.Key.StartsWith(pre, StringComparison.Ordinal)).ToList();
        var kitRes = index.ResByName.Where(k => k.Key.StartsWith(pre, StringComparison.Ordinal)).ToList();
        var bundles = kitEbx.SelectMany(k => k.Value).Select(e => e.Bundle).Concat(kitRes.SelectMany(k => k.Value).Select(r => r.Bundle))
            .Distinct().Where(b => NameOf(b).Name.Contains(pre, StringComparison.Ordinal))
            .OrderBy(b => NameOf(b).Name, StringComparer.Ordinal).ThenBy(b => b.Toc, StringComparer.OrdinalIgnoreCase).ToList();

        var guids = new Dictionary<Guid, Guid>();
        foreach (var (name, copies) in kitEbx)
            foreach (var e in copies) guids[EbxRiff.Parse(Dec(BundleIndex.ReadBytes(e.Data))).FileGuid] = Stable("ebx:" + Map(name));
        var rids = new Dictionary<ulong, ulong>();
        foreach (var (name, copies) in kitRes)
        {
            ulong rid = StableRid(Map(name));
            if (!usedRids.Add(rid)) throw new InvalidDataException("Resource id clash: " + Map(name));
            foreach (var r in copies) rids[r.Rid] = rid;
        }

        var chunkMap = new Dictionary<Guid, Guid>();
        var fullCache = new Dictionary<Guid, byte[]>();
        var seen = new HashSet<(string, string)>();
        var writes = new List<(string Cas, string Key, byte[] Bytes)>();
        var plans = new List<(BundleIndex.Bundle Source, string NewName, byte[] Manifest, List<(int Pair, string Cas, string Key)> Pairs, List<(Guid Id, string Cas, string Key)> Chunks)>();

        foreach (var b in bundles)
        {
            var source = NameOf(b);
            string newName = source.Name.Replace(pre, tgt, StringComparison.Ordinal).Replace(idFrom, idTo, StringComparison.Ordinal);
            if (archives[b.Toc].FindBundle(newName) is not null) throw new InvalidDataException("Bundle already exists: " + newName);
            var m = BundleManifest.Parse(BundleIndex.ReadBytes(b.Manifest));
            var records = BundleIndex.Records(b.Toc, tocFiles[b.Toc]).Single(r => r.RecordAt == b.RecordAt).Entries;
            if (records.Count != 1 + m.Ebx.Count + m.Res.Count + m.Chunks.Count) throw new InvalidDataException("Entry count mismatch: " + source.Name);
            var pairs = new List<(int, string, string)>();
            var chunks = new List<(Guid, string, string)>();
            void Put(int at, byte[] bytes, string key)
            {
                string cas = records[at].Cas;
                if (seen.Add((cas, key))) writes.Add((cas, key, bytes));
                pairs.Add((records[at].PairAt, cas, key));
            }
            int ebxN = m.Ebx.Count, resN = m.Res.Count;
            for (int i = 0; i < ebxN; i++)
            {
                var e = m.Ebx[i];
                if (!e.Name.StartsWith(pre, StringComparison.Ordinal)) continue;
                string mapped = Map(e.Name);
                var stored = BundleIndex.ReadBytes(records[1 + i]);
                var parsed = EbxRiff.Parse(Dec(stored));
                var raw = parsed.Rewrite(mapped, guids[parsed.FileGuid], guids);
                foreach (var (oldRid, newRid) in rids)
                {
                    var bytes = BitConverter.GetBytes(oldRid);
                    int at;
                    while ((at = raw.AsSpan().IndexOf(bytes)) >= 0) BitConverter.GetBytes(newRid).CopyTo(raw, at);
                }
                string text = Encoding.ASCII.GetString(raw);
                if (text.Contains(idFrom + "_", StringComparison.Ordinal) || text.Contains("/" + fromWord + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("Old kit name left in " + mapped);
                var encoded = EncodeLike(stored, raw);
                Put(1 + i, encoded, "ebx:" + mapped);
                m.Ebx[i] = e with { Name = mapped, OriginalSize = checked((uint)raw.Length), Sha1 = SHA1.HashData(encoded) };
                ebxCount++;
            }
            for (int i = 0; i < resN; i++)
            {
                var r = m.Res[i];
                if (!r.Name.StartsWith(pre, StringComparison.Ordinal)) continue;
                string mapped = Map(r.Name);
                var stored = BundleIndex.ReadBytes(records[1 + ebxN + i]);
                var raw = Dec(stored);
                var tex = TextureRes.Parse(raw);
                if (raw.Length < 116 || BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(112)) != Djb(r.Name))
                    throw new InvalidDataException("Name hash is not at offset 112: " + r.Name);
                if (!chunkMap.TryGetValue(tex.ChunkGuid, out var newChunk)) chunkMap[tex.ChunkGuid] = newChunk = Stable("chunk:" + mapped);
                newChunk.TryWriteBytes(raw.AsSpan(32, 16));
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(112), Djb(mapped));
                var encoded = EncodeLike(stored, raw);
                Put(1 + ebxN + i, encoded, "res:" + mapped);
                m.Res[i] = r with { Name = mapped, OriginalSize = checked((uint)raw.Length), Sha1 = SHA1.HashData(encoded), Rid = rids[r.Rid] };
                resCount++;

                int ci = m.Chunks.FindIndex(c => c.Id == tex.ChunkGuid || c.Id == newChunk);
                if (ci < 0) throw new InvalidDataException("Chunk is not in the manifest: " + r.Name);
                long fullSize = tex.PayloadSize * Ps4Codec.Slices(tex.Header);
                if (!fullCache.TryGetValue(tex.ChunkGuid, out var stockFull)) fullCache[tex.ChunkGuid] = stockFull = FullChunk(tex.ChunkGuid, fullSize);
                int entry = 1 + ebxN + resN + ci;
                string cas = records[entry].Cas;
                var sliceStored = BundleIndex.ReadBytes(records[entry]);
                var chunk = m.Chunks[ci];
                byte[] slice = sliceStored, full = stockFull;
                if (artByChunk.TryGetValue(tex.ChunkGuid, out var art))
                {
                    if (art.Length != fullSize || chunk.LogicalOffset + chunk.LogicalSize > art.Length)
                        throw new InvalidDataException($"{mapped}: artwork does not fill the source layout.");
                    full = EncodeLike(stockFull, art);
                    slice = EncodeLike(sliceStored, art.AsSpan((int)chunk.LogicalOffset, (int)chunk.LogicalSize).ToArray());
                }
                string sliceKey = "slice:" + newChunk, fullKey = "full:" + newChunk;
                if (seen.Add((cas, sliceKey))) writes.Add((cas, sliceKey, slice));
                if (seen.Add((cas, fullKey))) writes.Add((cas, fullKey, full));
                pairs.Add((records[entry].PairAt, cas, sliceKey));
                chunks.Add((newChunk, cas, fullKey));
                m.Chunks[ci] = chunk with { Id = newChunk, Sha1 = SHA1.HashData(slice) };
                byte[] oldHash = BitConverter.GetBytes(Djb(r.Name));
                int metaAt = m.ChunkMeta.AsSpan().IndexOf(oldHash);
                if (metaAt < 0) throw new InvalidDataException("Name hash is not in the chunk metadata: " + r.Name);
                BitConverter.GetBytes(Djb(mapped)).CopyTo(m.ChunkMeta, metaAt);
            }
            plans.Add((b, newName, m.Build(), pairs, chunks));
            bundleCount++;
        }
        foreach (var (name, _) in kitEbx)
            if (!plans.Any(p => BundleManifest.Parse(p.Manifest).Ebx.Any(e => e.Name == Map(name))))
                throw new InvalidDataException("Asset is not in any cloned bundle: " + name);
        if (where is null) return;

        foreach (var group in writes.GroupBy(w => w.Cas, StringComparer.OrdinalIgnoreCase))
        {
            using var fs = new FileStream(group.Key, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
            foreach (var w in group)
            {
                if (fs.Position + w.Bytes.Length > uint.MaxValue) throw new InvalidDataException("CAS file would pass 4 GB: " + group.Key);
                uint offset = checked((uint)fs.Position);
                fs.Write(w.Bytes);
                where[(group.Key, w.Key)] = (offset, (uint)w.Bytes.Length);
                appended[group.Key] = appended.GetValueOrDefault(group.Key) + w.Bytes.Length;
            }
            fs.Flush(true);
        }
        foreach (var p in plans)
        {
            uint manifestOffset;
            using (var fs = new FileStream(p.Source.Manifest.Cas, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16))
            {
                manifestOffset = checked((uint)fs.Position);
                fs.Write(p.Manifest);
                fs.Flush(true);
            }
            string toc = p.Source.Toc;
            var original = tocFiles[toc];
            var entries = BundleIndex.Records(toc, original).Single(r => r.RecordAt == p.Source.RecordAt).Entries;
            int pairsEnd = entries.Max(e => e.PairAt + 8);
            int flagsAt = p.Source.RecordAt + (int)BinaryPrimitives.ReadUInt32BigEndian(original.AsSpan(p.Source.RecordAt + 8, 4));
            int length = Math.Max(pairsEnd, flagsAt + entries.Count) - p.Source.RecordAt;
            var record = original.AsSpan(p.Source.RecordAt, length).ToArray();
            foreach (var (pair, cas, key) in p.Pairs) { var w = where[(cas, key)]; Pair(record, pair - p.Source.RecordAt, w.Offset, w.Size); }
            Pair(record, p.Source.Manifest.PairAt - p.Source.RecordAt, manifestOffset, (uint)p.Manifest.Length);
            if (!newBundles.TryGetValue(toc, out var list)) newBundles[toc] = list = [];
            list.Add(new TocArchive.NewBundle(p.NewName, NameOf(p.Source).SizeAndFlags, record));
            if (!newChunks.TryGetValue(toc, out var map)) newChunks[toc] = map = [];
            foreach (var (id, cas, key) in p.Chunks) { var w = where[(cas, key)]; map.TryAdd(id, new TocArchive.Chunk(id, LocationWord(cas), w.Offset, w.Size)); }
        }
    }

    void RebuildTocs()
    {
        foreach (var (toc, adds) in newBundles)
        {
            var rebuilt = archives[toc].Rebuild(adds, newChunks.TryGetValue(toc, out var chunks) ? chunks.Values : Enumerable.Empty<TocArchive.Chunk>());
            string tmp = toc + ".cims-kit-tmp";
            File.WriteAllBytes(tmp, rebuilt);
            var check = TocArchive.Parse(File.ReadAllBytes(tmp));
            foreach (var a in adds) if (check.FindBundle(a.Name) is null) throw new InvalidDataException("Bundle missing after TOC rebuild: " + a.Name);
            File.Move(tmp, toc, true);
            o.Say($"TOC rebuilt: {Path.GetRelativePath(Game, toc)} +{adds.Count} bundles");
        }
    }

    void ReportRegistry(IReadOnlyList<KitRegistration> kits)
    {
        foreach (var g in index.ResByName[PlayerTeamRegistry.ResourceName].GroupBy(r => (r.Data.Cas, r.Data.Offset)))
        {
            var r = g.First();
            var result = PlayerTeamRegistry.Register(Dec(BundleIndex.ReadBytes(r.Data)), r.Meta, kits);
            o.Say($"registry {Path.GetRelativePath(Game, g.Key.Cas)} ({g.Count()} copies): +{result.Assets} assets, +{result.Bundles} bundles");
        }
    }

    /// <summary>One new table per version; every bundle that carries the table gets the new payload and a new manifest.</summary>
    void Register(IReadOnlyList<KitRegistration> kits)
    {
        var fresh = BundleIndex.Build(Game, o.Say);
        var copies = fresh.ResByName[PlayerTeamRegistry.ResourceName];
        var built = new Dictionary<string, (byte[] Stored, PlayerTeamRegistry.Result Result)>();
        string Hash(byte[] x) => Convert.ToHexString(SHA1.HashData(x));
        foreach (var g in copies.GroupBy(r => Hash(BundleIndex.ReadBytes(r.Data))))
        {
            var result = PlayerTeamRegistry.Register(Dec(BundleIndex.ReadBytes(g.First().Data)), g.First().Meta, kits);
            var stored = ChunkBlocks.EncodeBlocks(result.Raw);
            if (!Dec(stored).AsSpan().SequenceEqual(result.Raw)) throw new InvalidDataException("player_team_brt did not encode back.");
            built[g.Key] = (stored, result);
            o.Say($"registry version ({g.Count()} copies): +{result.Assets} assets, +{result.Bundles} bundles");
        }
        var patched = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var locations = new Dictionary<(string, string), (uint Offset, uint Size)>();
        foreach (var r in copies)
        {
            string key = Hash(BundleIndex.ReadBytes(r.Data));
            var b = built[key];
            if (!locations.ContainsKey((key, r.Data.Cas)))
            {
                using var fs = new FileStream(r.Data.Cas, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
                locations[(key, r.Data.Cas)] = (checked((uint)fs.Position), (uint)b.Stored.Length);
                fs.Write(b.Stored);
                fs.Flush(true);
            }
            var at = locations[(key, r.Data.Cas)];
            var m = BundleManifest.Parse(BundleIndex.ReadBytes(r.Bundle.Manifest));
            int i = m.Res.FindIndex(x => x.Name == PlayerTeamRegistry.ResourceName);
            if (i < 0) throw new InvalidDataException("The table is missing from a bundle manifest.");
            m.Res[i] = m.Res[i] with { OriginalSize = checked((uint)b.Result.Raw.Length), Sha1 = SHA1.HashData(b.Stored), Meta = b.Result.Meta };
            byte[] manifest = m.Build();
            uint manifestOffset;
            using (var fs = new FileStream(r.Bundle.Manifest.Cas, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16))
            {
                manifestOffset = checked((uint)fs.Position);
                fs.Write(manifest);
            }
            if (!patched.TryGetValue(r.Data.Toc, out var toc)) patched[r.Data.Toc] = toc = File.ReadAllBytes(r.Data.Toc);
            Pair(toc, r.Data.PairAt, at.Offset, at.Size);
            Pair(toc, r.Bundle.Manifest.PairAt, manifestOffset, (uint)manifest.Length);
        }
        foreach (var (path, bytes) in patched)
        {
            string tmp = path + ".cims-kit-tmp";
            File.WriteAllBytes(tmp, bytes);
            TocArchive.Parse(File.ReadAllBytes(tmp));
            File.Move(tmp, path, true);
            o.Say("TOC patched: " + Path.GetRelativePath(Game, path));
        }
    }

    void ReadBack(IReadOnlyList<NewUniformAddition> additions, IReadOnlyList<KitRegistration> kits, Dictionary<string, byte[]> databases)
    {
        var check = new LegacyFiles(Game, settings);
        foreach (var (name, bytes) in databases)
            if (!check.Read(name).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Database read-back differs: " + name);

        var fresh = BundleIndex.Build(Game, o.Say);
        var bundles = BundleIndex.Tocs(Game).SelectMany(t => { try { return TocArchive.Parse(File.ReadAllBytes(t)).Bundles.Select(b => b.Name); } catch (InvalidDataException) { return []; } })
            .ToHashSet(StringComparer.Ordinal);
        int assets = 0;
        foreach (var a in additions)
        {
            string pre = a.SourcePrefix.TrimEnd('/') + "/", tgt = a.TargetPrefix.TrimEnd('/') + "/";
            string idFrom = KitRegistration.Identity(pre), idTo = KitRegistration.Identity(tgt);
            string Map(string n) => tgt + n[pre.Length..].Replace(idFrom, idTo, StringComparison.Ordinal);
            foreach (var name in index.EbxByName.Keys.Where(n => n.StartsWith(pre, StringComparison.Ordinal)))
            {
                if (!fresh.EbxByName.TryGetValue(Map(name), out var copies)) throw new InvalidDataException("Missing EBX after install: " + Map(name));
                foreach (var e in copies)
                    if (EbxRiff.Parse(Dec(BundleIndex.ReadBytes(e.Data))).Name != Map(name)) throw new InvalidDataException("EBX name stamp wrong: " + Map(name));
                assets++;
            }
            foreach (var name in index.ResByName.Keys.Where(n => n.StartsWith(pre, StringComparison.Ordinal)))
            {
                if (!fresh.ResByName.ContainsKey(Map(name))) throw new InvalidDataException("Missing RES after install: " + Map(name));
                foreach (var r in fresh.ResByName[Map(name)])
                    if (BinaryPrimitives.ReadUInt32LittleEndian(Dec(BundleIndex.ReadBytes(r.Data)).AsSpan(112)) != Djb(Map(name)))
                        throw new InvalidDataException("RES name hash wrong: " + Map(name));
                assets++;
            }
            foreach (var texture in a.Textures)
            {
                string target = texture.TargetSlot.Trim().Replace('\\', '/').TrimStart('/');
                if (artHashes.TryGetValue(target, out var hashes) && TextureSlots.ReadAll(Game, fresh, target).Any(sl => !hashes.Contains(Convert.ToHexString(SHA256.HashData(sl.Stock)))))
                    throw new InvalidDataException("Artwork read-back differs: " + target);
            }
        }
        int registered = 0;
        foreach (var g in fresh.ResByName[PlayerTeamRegistry.ResourceName].GroupBy(r => (r.Data.Cas, r.Data.Offset)))
        {
            var b = Dec(BundleIndex.ReadBytes(g.First().Data));
            if (!PlayerTeamRegistry.IsSorted(b)) throw new InvalidDataException("player_team_brt is not sorted.");
            foreach (var kit in kits)
            {
                var source = PlayerTeamRegistry.Rows(b, kit.SourceFolder.TrimEnd('/'));
                var target = PlayerTeamRegistry.Rows(b, kit.TargetFolder.TrimEnd('/'));
                if (source.Count != target.Count) throw new InvalidDataException($"{kit.TargetFolder}: {target.Count} rows, source has {source.Count}.");
                foreach (var row in target)
                {
                    if (!PlayerTeamRegistry.Contains(b, row.Leaf) || !PlayerTeamRegistry.Contains(b, row.Folder + "/" + row.Leaf))
                        throw new InvalidDataException("Lookup key missing: " + row.Leaf);
                    string bundle = PlayerTeamRegistry.BundleName(row.Bundle);
                    if (!bundles.Contains(bundle)) throw new InvalidDataException($"{row.Leaf}: bundle does not exist: {bundle}");
                    registered++;
                }
            }
        }
        o.Say($"read-back ok: {assets} assets, {registered} registered rows (both table versions), databases identical");
    }
}
