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
    public sealed record Options(string Target, ModFile Mod, string ArtDir, string? BackupRoot, bool Apply, Action<string> Say, int Workers = 0, bool CutExtraLayers = false, string JerseyName = DefaultJerseyName, bool RestoreAfterVerify = false, string? EquipmentSource = null, bool UseDonor = true);

    public const string DefaultJerseyName = "Fanatics 26-27";

    readonly Options o;
    readonly SuiteSettings settings;
    string Game => o.Target;
    BundleIndex index = null!;
    Dictionary<string, byte[]> tocFiles = null!;
    Dictionary<string, TocArchive> archives = null!;
    Dictionary<(string, ulong), TocArchive.Bundle> bundleNames = null!;
    Dictionary<(string Toc, int RecordAt), List<BundleIndex.Location>> bundleRecords = null!;
    HashSet<ulong> usedRids = null!;
    Lazy<Dictionary<Guid, List<BundleIndex.Location>>> globalChunks = null!;

    readonly Dictionary<string, List<TocArchive.NewBundle>> newBundles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Dictionary<Guid, TocArchive.Chunk>> newChunks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, long> appended = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Guid, byte[]> artByChunk = [];
    readonly Dictionary<string, HashSet<string>> artHashes = new(StringComparer.Ordinal);
    sealed record ExpandedSource(byte[] ResourceBefore, byte[] ResourceAfter, long OriginalSize, long ExpandedSize);
    readonly Dictionary<Guid, ExpandedSource> expandedByChunk = [];
    int bundleCount, ebxCount, resCount;
    string? backupPath;

    FullKitInstall(Options options, SuiteSettings suiteSettings) { o = options; settings = suiteSettings; }

    public static int Run(Options options, SuiteSettings settings) => new FullKitInstall(options, settings).Execute();

    /// <summary>Installs for the app: never throws, and reports the last status line and the backup that can undo it.</summary>
    public static (bool Ok, string Message, string? Backup) Apply(Options options, SuiteSettings settings)
    {
        string last = "";
        var install = new FullKitInstall(options with { Say = s => { last = s; options.Say(s); } }, settings);
        try { return (install.Execute() == 0, last, install.backupPath); }
        catch (Exception ex)
        {
            SuiteLog.Write("new-uniform apply failure: " + ex);
            return (false, ex.Message, null);
        }
    }

    static byte[] Dec(byte[] stored) { try { return ChunkBlocks.Parse(stored).Decode(); } catch (InvalidDataException) { return stored; } }
    static uint Djb(string s) { uint h = 5381; foreach (char c in s) h = (h * 33) ^ c; return h; }
    static Guid Stable(string id)
    {
        var b = SHA1.HashData(Encoding.ASCII.GetBytes("NHL27Convert:kit:" + id)).AsSpan(0, 16).ToArray();
        b[7] = (byte)((b[7] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b);
    }
    /// <summary>
    /// True when a bundle belongs to the source kit and is cloned with it. The writer and the read-back both use this, so
    /// a copy of a source asset inside another kit's bundle (an earlier broken install) is never expected in the clone.
    /// </summary>
    internal static bool OwnsBundle(string bundleName, string sourcePrefix) =>
        bundleName.Contains(sourcePrefix.TrimEnd('/') + "/", StringComparison.Ordinal);

    static ulong StableRid(string name) => BinaryPrimitives.ReadUInt64LittleEndian(SHA1.HashData(Encoding.ASCII.GetBytes("NHL27Convert:rid:" + name))) | 1; // The loader reads an even value as an already-resolved pointer; every stock id is odd.
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
    string ResolveArt(string path) => InstalledBrandingAudit.ResolveArtwork(path, o.ArtDir);

    int Execute()
    {
        Oodle.Ensure(settings);
        if (o.Apply && ApplyTarget.RunningGame() is { } running) { o.Say("REFUSED: " + running + " is running."); return 1; }
        if (o.Apply && ApplyTarget.Problems(Game) is { Count: > 0 } problems)
        {
            o.Say("REFUSED: " + string.Join("; ", problems));
            return 1;
        }
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
        var given = o.Mod.Additions ?? throw new InvalidDataException("The mod has no uniforms.");
        // Every kit is a renamed clone of one donor (Anaheim's regular home) with the team's NHL 27 art on top.
        // The user's manual matcher decisions are read from the NHL 27 layer files, so they apply before the donor step
        // replaces painted-on logo layers.
        var slotMapEarly = SlotMapStore.Load();
        if (o.UseDonor) MatcherOverrides.Apply(given, slotMapEarly, o.Say);
        var mods = o.UseDonor ? KitDonor.Retarget(given.ToList(), index, o.Say) : RegisteredKitSource.Normalize(given, index, o.Say);
        // Order matters: the user's matcher decisions first, then one layer per real NHL 27 logo, then any slot still missing.
        var slotMap = SlotMapStore.Load();
        int overrides = o.UseDonor ? 0 : MatcherOverrides.Apply(mods, slotMap, o.Say);
        if (overrides > 0) o.Say($"{overrides} manual matcher decision(s) applied");
        DuplicateLogoLayers.Apply(mods, array => SponsorSlots.PhysicalRoles(index, array), o.Say);
        ShoulderPatches.FitArt(mods, slotMap, o.Say);
        if (!o.UseDonor) mods = SponsorSlots.AddMissing(mods, index, o.Say);
        ShoulderPatches.PlaceSides(mods, slotMap, index, o.Say);
        foreach (var a in mods)
        {
            ManufacturerBranding.EnsureCoefficientArrays(a);
            string target = a.TargetPrefix.TrimEnd('/') + "/";
            if (index.EbxByName.Keys.Concat(index.ResByName.Keys).Any(n => n.StartsWith(target, StringComparison.Ordinal)))
            {
                o.Say($"REFUSED: {target} already exists. Restore a clean backup first.");
                return 1;
            }
        }
        var additionPlan = NewUniformAdditionPlanner.Build(mods, sources, metaPath, Exists);
        string equipmentSource = o.EquipmentSource ?? Path.Combine(AppContext.BaseDirectory, "data", "nhl27-equipment");
        var equipmentPlan = NewUniformEquipmentPlanner.Build(additionPlan.JerseyRows.Databases, metaPath, equipmentSource, additionPlan.Additions);
        var databases = equipmentPlan.Databases.ToDictionary(kv => RosterSyncRun.LegacyName(kv.Key), kv => kv.Value, StringComparer.Ordinal);
        o.Say($"NHL 27 equipment: {equipmentPlan.Rows.Count} planned rows, {equipmentPlan.InsertedRows} new rows across both databases; all 15 zones verified.");
        AddJerseyNames(additionPlan, databases, metaPath, legacy);

        tocFiles = index.Bundles.Select(b => b.Toc).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(t => t, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        archives = tocFiles.ToDictionary(kv => kv.Key, kv => TocArchive.Parse(kv.Value), StringComparer.OrdinalIgnoreCase);
        bundleNames = [];
        foreach (var (toc, archive) in archives) foreach (var b in archive.Bundles) bundleNames[(toc, b.RecordOffset)] = b;
        bundleRecords = [];
        foreach (var (toc, bytes) in tocFiles)
            foreach (var record in BundleIndex.Records(toc, bytes)) bundleRecords[(toc, record.RecordAt)] = record.Entries;
        usedRids = index.ResByName.Values.SelectMany(v => v).Select(r => r.Rid).ToHashSet();
        globalChunks = new(LoadGlobalChunks);

        var kits = additionPlan.Additions.Select(a => new KitRegistration(a.SourcePrefix.TrimEnd('/'), a.TargetPrefix.TrimEnd('/'), ReplaceExisting: true)).ToList();
        if (!o.Apply)
        {
            // A dry run checks every kit and lists every problem instead of stopping at the first one.
            var failures = new List<string>();
            foreach (var a in additionPlan.Additions)
            {
                try { PrepareArt(a); CloneKit(a, null); o.Say($"planned {a.Team} {a.SourceKit} -> variant {a.TargetVariant}"); }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or AggregateException)
                {
                    var inner = ex is AggregateException ag ? ag.Flatten().InnerExceptions[0] : ex;
                    failures.Add($"{a.Team} {a.SourceKit}: {inner.Message}");
                    o.Say($"FAILED {a.Team} {a.SourceKit}: {inner.Message}");
                }
                finally { artByChunk.Clear(); }
            }
            if (failures.Count > 0)
            {
                o.Say($"DRY RUN: {failures.Count} of {additionPlan.Additions.Count} kits failed. Nothing was written.");
                return 3;
            }
            ReportRegistry(kits);
            foreach (var (cas, bytes) in appended.OrderByDescending(kv => new FileInfo(kv.Key).Length + kv.Value).Take(5))
                o.Say($"projected {Path.GetRelativePath(Game, cas)}: {(new FileInfo(cas).Length + bytes) / 1048576.0:F0} MB of 4096 MB (+{bytes / 1048576.0:F0} MB)");
            o.Say($"total payload {appended.Values.Sum() / 1048576.0:F0} MB across {appended.Count} CAS files; over the limit: {appended.Count(kv => new FileInfo(kv.Key).Length + kv.Value > uint.MaxValue)}");
            o.Say($"DRY RUN: {additionPlan.Additions.Count} kits, {bundleCount} bundles, {ebxCount} EBX, {resCount} RES. Nothing was written.");
            return 0;
        }

        string backup = ApplyTarget.Backup(Game, o.BackupRoot);
        backupPath = backup;
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
            readBack: () => ReadBack(additionPlan, equipmentPlan, kits, databases, sources, metaPath),
            restoreAndVerify: () =>
            {
                ApplyTarget.RestoreFromBackup(Game, backup);
                var left = ApplyTarget.Compare(Game, backup);
                if (left.Count > 0) throw new InvalidOperationException("Rollback differences: " + string.Join("; ", left.Take(10)));
            });
        o.Say($"INSTALLED AND READ BACK: {additionPlan.Additions.Count} kits, {bundleCount} bundles, {ebxCount} EBX, {resCount} RES. Backup: {backup}");
        if (o.UseDonor && !o.RestoreAfterVerify)
        {
            // The owner's kit rules, read back from the installed files (FanaticsKitCheck). Reported, never undone.
            var after = BundleIndex.Build(Game);
            int failing = 0;
            foreach (var a in additionPlan.Additions)
            {
                var issues = FanaticsKitCheck.Check(Game, after, a.TargetPrefix, a.Team, a.SourceKit);
                if (issues.Count > 0) failing++;
                o.Say($"KIT CHECK {(issues.Count == 0 ? "OK  " : "FAIL")} {a.Team} {a.SourceKit}{(issues.Count == 0 ? "" : ": " + string.Join("; ", issues))}");
            }
            o.Say($"KIT CHECK: {additionPlan.Additions.Count - failing} of {additionPlan.Additions.Count} kits pass");
        }
        if (o.RestoreAfterVerify)
        {
            ApplyTarget.RestoreFromBackup(Game, backup);
            var left = ApplyTarget.Compare(Game, backup);
            if (left.Count > 0) throw new InvalidOperationException("Post-verification restore differences: " + string.Join("; ", left.Take(10)));
            o.Say("ISOLATED WRITE, READ-BACK AND RESTORE PASSED: no install differences remain.");
        }
        return 0;
    }

    /// <summary>
    /// The picker reads a vintage kit's name from the language key VINTAGE_{style}_{art}_{variant}, so a new variant has no
    /// name until that key exists. The new rows use the vintage label and every key goes into the English table.
    /// </summary>
    void AddJerseyNames(NewUniformAdditionPlan plan, Dictionary<string, byte[]> databases, string metaPath, LegacyFiles legacy)
    {
        const int VintageLabel = 6;
        const string English = "loc/eng_us.db";
        if (o.JerseyName.Length == 0) throw new InvalidDataException("A new uniform needs a jersey name.");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string name in plan.JerseyRows.ChangedDatabases)
        {
            string file = RosterSyncRun.LegacyName(name);
            var db = T3Db.Load((byte[])databases[file].Clone(), metaPath);
            var t = db.Tables["stockteamjerseys"];
            foreach (var a in plan.Additions)
            {
                int row = Enumerable.Range(0, t.InUse).Single(r => db.GetInt(t, r, "teamid") == a.TeamId
                    && db.GetInt(t, r, "style") == a.Style && db.GetInt(t, r, "variant") == a.TargetVariant);
                db.SetInt(t, row, "locstring", VintageLabel);
                keys.Add($"VINTAGE_{a.Style}_{db.GetInt(t, row, "teamartid")}_{a.TargetVariant}");
            }
            databases[file] = db.Save();
        }
        var strings = LanguageStrings.Load(legacy.Read(English));
        foreach (string key in keys)
        {
            if (strings.Find(key) is { } existing)
            {
                if (existing != o.JerseyName) throw new InvalidDataException($"{key} already names a uniform: {existing}");
                continue;
            }
            strings.Add(key, o.JerseyName);
        }
        byte[] built = strings.Build();
        var check = LanguageStrings.Load(built);
        foreach (string key in keys)
            if (check.Find(key) != o.JerseyName) throw new InvalidDataException("Language table did not read back: " + key);
        databases[English] = built;
        o.Say($"jersey name '{o.JerseyName}' on {keys.Count} kit(s)");
    }

    readonly Dictionary<Guid, TextureSlots.Slot> expandedSlots = [];

    /// <summary>
    /// How many logo positions a branding array's stamp defines (an added layout, else the stock stamp's largest copy).
    /// Every array must hold that many slices, or a positioned logo has no slice to draw from.
    /// </summary>
    int StampSlots(NewUniformAddition a, string arraySource)
    {
        string stamp = UniformPhysicalLayouts.StampForArray(arraySource.Trim().Replace('\\', '/').TrimStart('/'));
        if (a.HotspotLayouts.SingleOrDefault(l => l.SourceSlot == stamp) is { } added) return added.Branding.Count;
        int most = 0;
        foreach (var bundle in (index.EbxByName.GetValueOrDefault(stamp) ?? []).Select(e => e.Bundle).DistinctBy(b => (b.Toc, b.RecordAt)))
        {
            try { most = Math.Max(most, UniformHotspots.Roles(UniformPhysicalLayouts.ReadStamp(index, stamp, bundle), stamp).Count); }
            catch (InvalidDataException) { }
        }
        return most;
    }

    /// <summary>Expands one source copy once; the clone receives new structural slots, never the donor's pixels.</summary>
    TextureSlots.Slot EnsureCapacity(NewUniformAddition a, string target, string role, int need,
        TextureSlots.Slot sourceSlot, IReadOnlyList<TextureSlots.Slot> siblings, int codecThreads)
    {
        int have = Ps4Codec.Slices(sourceSlot.Res.Header);
        if (have >= need || o.CutExtraLayers) return sourceSlot;
        if (role.Length == 0) throw new InvalidDataException($"{sourceSlot.Name}: only branding arrays can gain structural slots.");
        lock (expandedSlots)
        {
            if (expandedSlots.TryGetValue(sourceSlot.Chunk, out var cached))
            {
                if (!expandedByChunk[sourceSlot.Chunk].ResourceBefore.AsSpan().SequenceEqual(sourceSlot.Res.Header))
                    throw new InvalidDataException($"Conflicting source layouts for shared chunk {sourceSlot.Chunk}.");
                return cached with { Name = sourceSlot.Name };
            }
            var donor = InstalledArrayExpansion.StructuralDonor(Game, index, sourceSlot, siblings, role, need)
                ?? throw new InvalidDataException($"{a.Team} {a.SourceKit}: {sourceSlot.Name} chunk {sourceSlot.Chunk} has {have} slots, "
                    + $"but the artwork needs {need}, and no compatible structural donor was found.");
            var expanded = InstalledArrayExpansion.Expand(sourceSlot, donor, role, codecThreads);
            var expansion = new ExpandedSource((byte[])sourceSlot.Res.Header.Clone(), expanded.Res.Header,
                sourceSlot.Res.PayloadSize * have, expanded.Stock.LongLength);
            if (expandedByChunk.TryGetValue(sourceSlot.Chunk, out var existing))
            {
                if (!existing.ResourceBefore.AsSpan().SequenceEqual(expansion.ResourceBefore)
                    || !existing.ResourceAfter.AsSpan().SequenceEqual(expansion.ResourceAfter)
                    || existing.OriginalSize != expansion.OriginalSize || existing.ExpandedSize != expansion.ExpandedSize)
                    throw new InvalidDataException($"Conflicting structural expansions for source chunk {sourceSlot.Chunk}.");
            }
            else expandedByChunk.Add(sourceSlot.Chunk, expansion);
            expandedSlots.Add(sourceSlot.Chunk, expanded);
            o.Say($"SAFE SLOT EXPANSION: {a.Team} {a.SourceKit} {target[(target.LastIndexOf('/') + 1)..]} chunk {sourceSlot.Chunk}: "
                + $"{have} -> {Ps4Codec.Slices(expanded.Res.Header)}; team slices preserved, added slots blank, no donor pixels retained.");
            return expanded;
        }
    }

    /// <summary>
    /// Plans each physical source copy independently, after any required expansion. Equal-depth copies can contain
    /// different stock pictures, so a name/depth key can put a sponsor in the wrong slot in a companion copy.
    /// </summary>
    Dictionary<Guid, UniformLayerPlacement> PlanLayerPlacement(NewUniformAddition a, int codecThreads)
    {
        var plans = new Dictionary<Guid, UniformLayerPlacement>();
        foreach (var color in a.Textures.Where(t => t.SourceSlot.EndsWith("_color_array", StringComparison.Ordinal)
                     && t.Artwork.Layers is { Count: > 0 }))
        {
            string stem = color.SourceSlot[..^"_color_array".Length];
            var normal = a.Textures.SingleOrDefault(t => t.SourceSlot == stem + "_normal_array")
                ?? throw new InvalidDataException($"{color.TargetSlot}: its converted normal array is missing.");
            var coeff = a.Textures.SingleOrDefault(t => t.SourceSlot == stem + "_coeff_array")
                ?? throw new InvalidDataException($"{color.TargetSlot}: its converted coefficient array is missing.");
            if (normal.Artwork.Layers is not { Count: > 0 }) throw new InvalidDataException($"{normal.TargetSlot}: converted layers are missing.");
            var sourceColors = TextureSlots.ReadAll(Game, index, color.SourceSlot);
            var sourceNormals = TextureSlots.ReadAll(Game, index, normal.SourceSlot);
            var sourceCoeffs = TextureSlots.ReadAll(Game, index, coeff.SourceSlot);
            string sourceStamp = UniformPhysicalLayouts.StampForArray(color.SourceSlot);
            int overrideSlots = StampSlots(a, color.SourceSlot);
            int required = Math.Max(color.Artwork.Layers!.Keys.Max() + 1,
                Math.Max(color.RequiredRoles?.Count ?? 0, Math.Max(color.DonorRoles?.Count ?? 0, overrideSlots)));
            foreach (var originalColor in sourceColors)
            {
                var originalNormal = InstalledBrandingAudit.Companion(sourceNormals, originalColor.Res,
                    "source normal", normal.SourceSlot, normal.Artwork.Layers.Count);
                var originalCoeff = InstalledBrandingAudit.Companion(sourceCoeffs, originalColor.Res,
                    "source coefficient", coeff.SourceSlot, color.Artwork.Layers.Count);
                var colorSlot = EnsureCapacity(a, color.TargetSlot, "color", required, originalColor, sourceColors, codecThreads);
                var normalSlot = EnsureCapacity(a, normal.TargetSlot, "normal", required, originalNormal, sourceNormals, codecThreads);
                var coeffSlot = EnsureCapacity(a, coeff.TargetSlot, "coeff", required, originalCoeff, sourceCoeffs, codecThreads);
                int have = Ps4Codec.Slices(colorSlot.Res.Header);
                int normalHave = Ps4Codec.Slices(normalSlot.Res.Header), coeffHave = Ps4Codec.Slices(coeffSlot.Res.Header);
                int capacity = Math.Min(have, Math.Min(normalHave, coeffHave));
                var sourceBundle = UniformPhysicalLayouts.BundleForTexture(index, originalColor, color.SourceSlot);
                var physical = UniformPhysicalLayouts.Resolve(index, a, color, capacity, sourceBundle);
                if (normal.DonorRoles is { } normalRoles && !normalRoles.SequenceEqual(physical.Roles, StringComparer.Ordinal))
                    throw new InvalidDataException($"{normal.SourceSlot}: color and normal physical roles differ.");
                var stock = Ps4Codec.DecodeSlices(colorSlot.Res, colorSlot.Stock);
                var art = InstalledBrandingAudit.LoadArtwork(color.Artwork.Layers, have, colorSlot.Res, ResolveArt, o.Workers);
                var placement = UniformBrandingLayers.Plan(color, art, stock, capacity, physical.Roles);
                // Every logo must land in a slot of its own kind, checked against the same stamp the game reads.
                if (color.RequiredRoles is { } artRoles
                    && UniformBrandingLayers.Misplaced(artRoles, color.Artwork.Layers.Keys.ToList(), physical.Roles, placement, color.PinnedSlots) is { Count: > 0 } wrong)
                    throw new InvalidDataException($"{a.Team} {a.SourceKit} {color.TargetSlot[(color.TargetSlot.LastIndexOf('/') + 1)..]}: "
                        + string.Join("; ", wrong) + ". Fix it in the slot map or the Shoulder patches choice.");
                foreach (var slot in new[] { colorSlot, normalSlot, coeffSlot })
                {
                    if (plans.TryGetValue(slot.Chunk, out var existing)
                        && (existing.Moves.Count != placement.Moves.Count
                            || existing.Moves.Any(move => placement.Destination(move.Key) != move.Value)
                            || !existing.KeepStock.SetEquals(placement.KeepStock)
                            || !existing.BlankSources.SetEquals(placement.BlankSources)))
                        throw new InvalidDataException($"Conflicting artwork placements for shared source chunk {slot.Chunk}.");
                    plans[slot.Chunk] = placement;
                }
                if (placement.Moves.Count > 0)
                    o.Say($"{a.Team} {a.SourceKit} {color.TargetSlot[(color.TargetSlot.LastIndexOf('/') + 1)..]} "
                        + $"chunk {colorSlot.Chunk} ({have} layers): layer(s) "
                        + string.Join(", ", placement.Moves.Select(m => $"{m.Key} to slot {m.Value}")) + " matched the stock art");
            }
        }
        return plans;
    }

    /// <summary>Encodes every mapped texture into its same-team source slot layout; unused array layers are blanked.</summary>
    void PrepareArt(NewUniformAddition a)
    {
        var cache = new TexturePayloadCache();
        int jobs = Math.Clamp(o.Workers / 6, 1, 4);
        int codecThreads = o.Workers <= 0 ? 0 : Math.Max(1, o.Workers / jobs);
        int cacheHits = 0, encoded = 0;
        var prepared = new System.Collections.Concurrent.ConcurrentBag<(string Target, bool HasArtwork, List<(Guid Chunk, byte[] Raw, string Hash)> Copies)>();
        var layerPlans = PlanLayerPlacement(a, codecThreads);
        Parallel.For(0, a.Textures.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i =>
        {
            var texture = a.Textures[i];
            string source = texture.SourceSlot.Trim().Replace('\\', '/').TrimStart('/');
            string target = texture.TargetSlot.Trim().Replace('\\', '/').TrimStart('/');
            var art = texture.Artwork;
            var resolved = art with
            {
                Slot = source,
                Source = ResolveArt(art.Source),
                Layers = art.Layers?.ToDictionary(kv => kv.Key, kv => ResolveArt(kv.Value)),
            };
            if (resolved.Source.Length == 0 && resolved.Layers is not { Count: > 0 })
            {
                prepared.Add((target, false, []));
                return;
            }
            // An update can carry the same texture again with a different layout, and the game may read either copy.
            var slots = TextureSlots.ReadAll(Game, index, source);
            int need = Math.Max(resolved.Layers is { Count: > 0 } ? resolved.Layers.Keys.Max() + 1 : 1,
                Math.Max(texture.RequiredRoles?.Count ?? 0, texture.DonorRoles?.Count ?? 0));
            string lower = target.ToLowerInvariant();
            // An added hotspot (a sponsor slot) makes the layout longer than the artwork: every array must reach it.
            string sourceLeaf = source[(source.LastIndexOf('/') + 1)..];
            if (sourceLeaf.StartsWith("branding_", StringComparison.Ordinal) && sourceLeaf.EndsWith("_array", StringComparison.Ordinal))
                need = Math.Max(need, StampSlots(a, source));
            string arrayRole = lower.EndsWith("_color_array", StringComparison.Ordinal) ? "color"
                : lower.EndsWith("_normal_array", StringComparison.Ordinal) ? "normal"
                : lower.EndsWith("_coeff_array", StringComparison.Ordinal) ? "coeff" : "";
            bool blankable = arrayRole.Length > 0;
            var copies = new List<(Guid Chunk, byte[] Raw, string Hash)>();
            foreach (var sourceSlot in slots)
            {
                var slot = EnsureCapacity(a, target, arrayRole, need, sourceSlot, slots, codecThreads);
                int have = Ps4Codec.Slices(slot.Res.Header);
                var copy = resolved;
                if (have < need)
                {
                    if (!o.CutExtraLayers) throw new InvalidDataException($"{source}: its verified structural expansion is incomplete.");
                    copy = resolved with { Layers = resolved.Layers!.Where(kv => kv.Key < have).ToDictionary(kv => kv.Key, kv => kv.Value) };
                    o.Say($"LAYER CUT: {target}: copy has {have} layer(s); artwork layer(s) {have}-{need - 1} left out");
                }
                HashSet<int> keepStock = [];
                if (copy.Layers is { Count: > 0 } && layerPlans.TryGetValue(slot.Chunk, out var layerPlan))
                {
                    copy = copy with { Layers = copy.Layers.Where(kv => !layerPlan.BlankSources.Contains(kv.Key))
                        .ToDictionary(kv => layerPlan.Destination(kv.Key), kv => kv.Value) };
                    keepStock = layerPlan.KeepStock.ToHashSet();
                }
                string baseKey = TexturePayloadCache.Key(slot, copy, out var stockForAlpha);
                // Every logo slot without art is blanked, including an array whose layers are all deliberately empty
                // (painted-on donor kits): an untouched slice would show the donor's own logo.
                bool blankUnused = blankable
                    && Enumerable.Range(0, have).Any(layer => !(copy.Layers?.ContainsKey(layer) ?? false) && !keepStock.Contains(layer));
                // Blanked output is not interchangeable with TextureSlots' preserve-unused-layers output, even when
                // their source artwork is identical. Give it a separate content-addressed cache namespace.
                string key = blankUnused ? Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(baseKey + ":full-kit-blank-v3-" + arrayRole + ":" + string.Join(",", keepStock.Order())))) : baseKey;
                byte[]? raw = cache.Load(key);
                if (raw is not null) Interlocked.Increment(ref cacheHits);
                else
                {
                    raw = TextureSlots.Build(slot, copy, codecThreads, stockForAlpha);
                    if (blankUnused)
                    {
                        var blank = UniformBrandingLayers.Blank(slot.Res.Width, slot.Res.Height, arrayRole);
                        var unused = Enumerable.Range(0, have).Where(layer => !(copy.Layers?.ContainsKey(layer) ?? false) && !keepStock.Contains(layer)).ToDictionary(layer => layer, _ => blank);
                        raw = Ps4Codec.Build(slot.Res, raw, unused, codecThreads);
                    }
                    cache.Store(key, raw, keepPayload: true);
                    Interlocked.Increment(ref encoded);
                }
                copies.Add((slot.Chunk, raw, Convert.ToHexString(SHA256.HashData(raw))));
            }
            prepared.Add((target, true, copies));
        });
        cache.Save();
        foreach (var result in prepared)
        {
            if (!result.HasArtwork) continue;
            artHashes[result.Target] = result.Copies.Select(c => c.Hash).ToHashSet(StringComparer.Ordinal);
            foreach (var copy in result.Copies)
            {
                if (artByChunk.TryGetValue(copy.Chunk, out var existing) && !existing.AsSpan().SequenceEqual(copy.Raw))
                    throw new InvalidDataException($"Two source textures require different data in shared chunk {copy.Chunk}.");
                artByChunk[copy.Chunk] = copy.Raw;
            }
        }
        o.Say($"prepared {a.Team} {a.SourceKit}: {cacheHits} verified cache hits, {encoded} encoded ({jobs} jobs, {codecThreads} codec threads)");
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
        string teamFrom = pre.TrimEnd('/').Split('/')[^3], teamTo = tgt.TrimEnd('/').Split('/')[^3];
        string Map(string n) => tgt + n[pre.Length..].Replace(idFrom, idTo, StringComparison.Ordinal);

        var kitEbx = index.EbxByName.Where(k => k.Key.StartsWith(pre, StringComparison.Ordinal)).ToList();
        var kitRes = index.ResByName.Where(k => k.Key.StartsWith(pre, StringComparison.Ordinal)).ToList();
        var bundles = kitEbx.SelectMany(k => k.Value).Select(e => e.Bundle).Concat(kitRes.SelectMany(k => k.Value).Select(r => r.Bundle))
            .Distinct().Where(b => OwnsBundle(NameOf(b).Name, pre))
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
            var records = bundleRecords[(b.Toc, b.RecordAt)];
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
                byte[] sourceRaw = Dec(stored);
                if (a.HotspotLayouts.SingleOrDefault(l => l.SourceSlot == e.Name) is { } hotspots)
                {
                    sourceRaw = UniformHotspots.Replace(sourceRaw, hotspots);
                    if (!UniformPhysicalLayouts.SameLayout(UniformHotspots.Capture(sourceRaw, mapped), hotspots))
                        throw new InvalidDataException($"{mapped}: requested hotspot ids, names or coordinates did not read back.");
                }
                if (a.FontPlacement.TryGetValue(e.Name, out var teamStamp))
                {
                    if (teamStamp.StartsWith("<hotspots", StringComparison.Ordinal)) sourceRaw = UniformHotspots.CopyFontPlacement(sourceRaw, teamStamp);
                    else if (index.EbxByName.TryGetValue(teamStamp, out var teamEntries))
                        sourceRaw = UniformHotspots.CopyFontPlacement(sourceRaw, Dec(BundleIndex.ReadBytes(teamEntries[0].Data)));
                }
                var parsed = EbxRiff.Parse(sourceRaw);
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
                // A donor clone must not keep a path into the donor team's folder (it would load the donor's art).
                if (teamFrom != teamTo && text.Contains("/nhl/" + teamFrom + "/", StringComparison.Ordinal))
                    throw new InvalidDataException($"{mapped}: still points into the donor team's folder ({teamFrom}).");
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
                Guid sourceChunk = tex.ChunkGuid;
                expandedByChunk.TryGetValue(sourceChunk, out var expansion);
                if (raw.Length < 116 || BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(112)) != Djb(r.Name))
                    throw new InvalidDataException("Name hash is not at offset 112: " + r.Name);
                if (expansion is not null)
                {
                    if (!raw.AsSpan().SequenceEqual(expansion.ResourceBefore))
                        throw new InvalidDataException($"{mapped}: the source resource differs from its verified expansion plan.");
                    raw = (byte[])expansion.ResourceAfter.Clone();
                    tex = TextureRes.Parse(raw);
                }
                if (!chunkMap.TryGetValue(sourceChunk, out var newChunk)) chunkMap[sourceChunk] = newChunk = Stable("chunk:" + mapped + ":" + sourceChunk);
                newChunk.TryWriteBytes(raw.AsSpan(32, 16));
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(112), Djb(mapped));
                var encoded = EncodeLike(stored, raw);
                Put(1 + ebxN + i, encoded, "res:" + mapped);
                m.Res[i] = r with { Name = mapped, OriginalSize = checked((uint)raw.Length), Sha1 = SHA1.HashData(encoded), Rid = rids[r.Rid] };
                resCount++;

                int ci = m.Chunks.FindIndex(c => c.Id == sourceChunk || c.Id == newChunk);
                if (ci < 0) throw new InvalidDataException("Chunk is not in the manifest: " + r.Name);
                long sourceSize = expansion?.OriginalSize ?? tex.PayloadSize * Ps4Codec.Slices(tex.Header);
                long fullSize = expansion?.ExpandedSize ?? sourceSize;
                if (!fullCache.TryGetValue(sourceChunk, out var stockFull)) fullCache[sourceChunk] = stockFull = FullChunk(sourceChunk, sourceSize);
                int entry = 1 + ebxN + resN + ci;
                string cas = records[entry].Cas;
                var sliceStored = BundleIndex.ReadBytes(records[entry]);
                var chunk = m.Chunks[ci];
                if (expansion is not null && (chunk.LogicalOffset != 0 || chunk.LogicalSize != sourceSize))
                    throw new InvalidDataException($"{mapped}: an expanded array needs one complete source chunk range, not a partial slice.");
                byte[] slice = sliceStored, full = stockFull;
                if (artByChunk.TryGetValue(sourceChunk, out var art))
                {
                    if (art.Length != fullSize || chunk.LogicalOffset + chunk.LogicalSize > art.Length)
                        throw new InvalidDataException($"{mapped}: artwork does not fill the verified source or expanded layout.");
                    full = EncodeLike(stockFull, art);
                    // Expansion changes the declared logical size, so every full bundle copy needs the expanded payload too.
                    // Reencoding against the old one-slice stored payload would leave a short copy behind.
                    slice = expansion is not null ? full
                        : EncodeLike(sliceStored, art.AsSpan((int)chunk.LogicalOffset, (int)chunk.LogicalSize).ToArray());
                }
                string sliceKey = "slice:" + newChunk, fullKey = "full:" + newChunk;
                if (seen.Add((cas, sliceKey))) writes.Add((cas, sliceKey, slice));
                if (seen.Add((cas, fullKey))) writes.Add((cas, fullKey, full));
                pairs.Add((records[entry].PairAt, cas, sliceKey));
                chunks.Add((newChunk, cas, fullKey));
                m.Chunks[ci] = chunk with { Id = newChunk, LogicalSize = expansion is null ? chunk.LogicalSize : checked((uint)fullSize), Sha1 = SHA1.HashData(slice) };
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
        if (where is null)
        {
            foreach (var w in writes) appended[w.Cas] = appended.GetValueOrDefault(w.Cas) + w.Bytes.Length;
            return;
        }

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
            var entries = bundleRecords[(toc, p.Source.RecordAt)];
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
        var storedCopies = new Dictionary<(string Cas, uint Offset), byte[]>();
        byte[] Stored(BundleIndex.Res r)
        {
            var key = (r.Data.Cas, r.Data.Offset);
            if (!storedCopies.TryGetValue(key, out var bytes)) storedCopies[key] = bytes = BundleIndex.ReadBytes(r.Data);
            return bytes;
        }
        string Hash(byte[] x) => Convert.ToHexString(SHA1.HashData(x));
        string Version(BundleIndex.Res r) => Hash(Stored(r));
        var built = new Dictionary<string, (byte[] Stored, PlayerTeamRegistry.Result Result)>();
        foreach (var g in copies.GroupBy(Version))
        {
            var result = PlayerTeamRegistry.Register(Dec(Stored(g.First())), g.First().Meta, kits);
            var stored = ChunkBlocks.EncodeBlocks(result.Raw);
            if (!Dec(stored).AsSpan().SequenceEqual(result.Raw)) throw new InvalidDataException("player_team_brt did not encode back.");
            built[g.Key] = (stored, result);
            o.Say($"registry version ({g.Count()} copies): +{result.Assets} assets, +{result.Bundles} bundles");
        }

        // Hundreds of bundles share the same registry payload and often the same manifest. Build each distinct output
        // once, then append each CAS with one open handle. Every TOC reference still gets its own verified pointer.
        var registryLocations = new Dictionary<(string Version, string Cas), (uint Offset, uint Size)>();
        foreach (var g in copies.GroupBy(r => (Version: Version(r), r.Data.Cas)))
        {
            var b = built[g.Key.Version];
            using var fs = new FileStream(g.Key.Cas, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
            uint offset = checked((uint)fs.Position);
            fs.Write(b.Stored);
            fs.Flush(true);
            registryLocations[g.Key] = (offset, (uint)b.Stored.Length);
        }

        var manifests = new Dictionary<(string Version, string Cas, uint Offset), byte[]>();
        foreach (var g in copies.GroupBy(r => (Version: Version(r), r.Bundle.Manifest.Cas, r.Bundle.Manifest.Offset)))
        {
            var b = built[g.Key.Version];
            var m = BundleManifest.Parse(BundleIndex.ReadBytes(g.First().Bundle.Manifest));
            int i = m.Res.FindIndex(x => x.Name == PlayerTeamRegistry.ResourceName);
            if (i < 0) throw new InvalidDataException("The table is missing from a bundle manifest.");
            m.Res[i] = m.Res[i] with { OriginalSize = checked((uint)b.Result.Raw.Length), Sha1 = SHA1.HashData(b.Stored), Meta = b.Result.Meta };
            manifests[g.Key] = m.Build();
        }
        var manifestLocations = new Dictionary<(string Version, string Cas, uint Offset), (uint Offset, uint Size)>();
        foreach (var g in manifests.GroupBy(kv => kv.Key.Cas, StringComparer.OrdinalIgnoreCase))
        {
            using var fs = new FileStream(g.Key, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 20);
            foreach (var (key, bytes) in g)
            {
                uint offset = checked((uint)fs.Position);
                fs.Write(bytes);
                manifestLocations[key] = (offset, (uint)bytes.Length);
            }
            fs.Flush(true);
        }

        var patched = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in copies)
        {
            string version = Version(r);
            var data = registryLocations[(version, r.Data.Cas)];
            var manifest = manifestLocations[(version, r.Bundle.Manifest.Cas, r.Bundle.Manifest.Offset)];
            if (!patched.TryGetValue(r.Data.Toc, out var toc)) patched[r.Data.Toc] = toc = File.ReadAllBytes(r.Data.Toc);
            Pair(toc, r.Data.PairAt, data.Offset, data.Size);
            Pair(toc, r.Bundle.Manifest.PairAt, manifest.Offset, manifest.Size);
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

    void ReadBack(NewUniformAdditionPlan plan, NewUniformEquipmentPlan equipmentPlan, IReadOnlyList<KitRegistration> kits,
        Dictionary<string, byte[]> databases, IReadOnlyDictionary<string, byte[]> sources, string metaPath)
    {
        var check = new LegacyFiles(Game, settings);
        foreach (var (name, bytes) in databases)
            if (!check.Read(name).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Database read-back differs: " + name);

        // Verify row counts independently, then validate every NHL 27 donor key and all 15 color zones.
        int databaseRows = 0, equipmentRows = 0;
        foreach (string name in plan.JerseyRows.ChangedDatabases)
        {
            var before = T3Db.Load((byte[])sources[name].Clone(), metaPath);
            var after = T3Db.Load((byte[])databases[RosterSyncRun.LegacyName(name)].Clone(), metaPath);
            int expectedJerseys = plan.JerseyRows.Rows.Count(r => r.Database == name && !r.AlreadyExists);
            int actualJerseys = after.Tables[JerseyRowPlanner.TableName].InUse - before.Tables[JerseyRowPlanner.TableName].InUse;
            if (actualJerseys != expectedJerseys) throw new InvalidDataException(
                $"{name}: expected {expectedJerseys} new jersey rows, found {actualJerseys}.");
            int expectedEquipment = equipmentPlan.Rows.Count(r => r.Database == name && !r.AlreadyExists);
            int actualEquipment = after.Tables[NewUniformEquipmentPlan.TableName].InUse - before.Tables[NewUniformEquipmentPlan.TableName].InUse;
            if (actualEquipment != expectedEquipment) throw new InvalidDataException(
                $"{name}: expected {expectedEquipment} new equipment rows, found {actualEquipment}.");
            equipmentPlan.Verify(name, after);
            databaseRows += actualJerseys;
            equipmentRows += actualEquipment;
        }

        var fresh = BundleIndex.Build(Game, o.Say);
        var freshArchives = fresh.Bundles.Select(b => b.Toc).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(t => t, t => TocArchive.Parse(File.ReadAllBytes(t)), StringComparer.OrdinalIgnoreCase);
        string FreshName(BundleIndex.Bundle b) => freshArchives[b.Toc].Bundles.Single(x =>
            x.RecordOffset == (ulong)(b.RecordAt - TocFile.HeaderSize)).Name;
        string FreshKey(BundleIndex.Bundle b) => b.Toc + "\n" + FreshName(b);
        var bundles = freshArchives.Values.SelectMany(a => a.Bundles.Select(b => b.Name)).ToHashSet(StringComparer.Ordinal);

        byte[] FreshFullChunk(Guid id, long size)
        {
            foreach (var c in fresh.ChunksById.GetValueOrDefault(id) ?? [])
            {
                if (c.LogicalOffset != 0 || c.LogicalSize != size) continue;
                byte[] raw = Dec(BundleIndex.ReadBytes(c.Data));
                if (raw.Length == size) return raw;
            }
            foreach (var (toc, archive) in freshArchives)
            {
                if (archive.FindChunk(id) is not { } c) continue;
                string cas = BundleIndex.CasPath(Game, (c.LocationWord & 0x10000) != 0,
                    (int)((c.LocationWord >> 8) & 0xFF), (int)(c.LocationWord & 0xFF));
                byte[] raw = Dec(BundleIndex.ReadBytes(new BundleIndex.Location(toc, 0, cas, c.Offset, c.Size)));
                if (raw.Length == size) return raw;
            }
            throw new InvalidDataException("No complete cloned chunk found for " + id);
        }

        void VerifyChunkReferences(Guid id, byte[] full, string mapped)
        {
            int bundleReferences = 0, globalReferences = 0;
            foreach (var chunk in fresh.ChunksById.GetValueOrDefault(id) ?? [])
            {
                if (chunk.LogicalOffset + (long)chunk.LogicalSize > full.LongLength)
                    throw new InvalidDataException($"{mapped}: a bundle chunk range exceeds its cloned texture.");
                byte[] raw = Dec(BundleIndex.ReadBytes(chunk.Data));
                if (raw.LongLength != chunk.LogicalSize
                    || !raw.AsSpan().SequenceEqual(full.AsSpan(checked((int)chunk.LogicalOffset), checked((int)chunk.LogicalSize))))
                    throw new InvalidDataException($"{mapped}: a bundle payload is short or differs from the complete cloned texture.");
                bundleReferences++;
            }
            foreach (var (toc, archive) in freshArchives)
            {
                if (archive.FindChunk(id) is not { } chunk) continue;
                string cas = BundleIndex.CasPath(Game, (chunk.LocationWord & 0x10000) != 0,
                    (int)((chunk.LocationWord >> 8) & 0xFF), (int)(chunk.LocationWord & 0xFF));
                byte[] raw = Dec(BundleIndex.ReadBytes(new BundleIndex.Location(toc, 0, cas, chunk.Offset, chunk.Size)));
                if (!raw.AsSpan().SequenceEqual(full))
                    throw new InvalidDataException($"{mapped}: a global chunk payload is short or differs from the complete cloned texture.");
                globalReferences++;
            }
            if (bundleReferences == 0 && globalReferences == 0)
                throw new InvalidDataException($"{mapped}: the cloned texture has no readable chunk references.");
        }

        int assets = 0;
        foreach (var a in plan.Additions)
        {
            string pre = a.SourcePrefix.TrimEnd('/') + "/", tgt = a.TargetPrefix.TrimEnd('/') + "/";
            string idFrom = KitRegistration.Identity(pre), idTo = KitRegistration.Identity(tgt);
            string Map(string n) => tgt + n[pre.Length..].Replace(idFrom, idTo, StringComparison.Ordinal);
            string MapBundle(string n) => n.Replace(pre, tgt, StringComparison.Ordinal).Replace(idFrom, idTo, StringComparison.Ordinal);
            string SourceKey(BundleIndex.Bundle b) => b.Toc + "\n" + MapBundle(NameOf(b).Name);
            // Only the kit's own bundles are cloned (see CloneKit). A copy of a source asset inside another kit's bundle
            // (left by an earlier broken install, e.g. Montreal arrays inside Detroit's) is not part of this kit.
            bool OwnBundle(BundleIndex.Bundle b) => OwnsBundle(NameOf(b).Name, pre);
            var sourceEbx = index.EbxByName.Where(kv => kv.Key.StartsWith(pre, StringComparison.Ordinal)).SelectMany(kv => kv.Value).ToList();
            var sourceRes = index.ResByName.Where(kv => kv.Key.StartsWith(pre, StringComparison.Ordinal)).SelectMany(kv => kv.Value).ToList();
            var guids = new Dictionary<Guid, Guid>();
            foreach (var e in sourceEbx)
                guids[EbxRiff.Parse(Dec(BundleIndex.ReadBytes(e.Data))).FileGuid] = Stable("ebx:" + Map(e.Name));
            var rids = sourceRes.GroupBy(r => r.Rid).ToDictionary(g => g.Key, g => StableRid(Map(g.First().Name)));

            foreach (var source in sourceEbx.Where(e => OwnBundle(e.Bundle)))
            {
                string mapped = Map(source.Name);
                if (!fresh.EbxByName.TryGetValue(mapped, out var copies)) throw new InvalidDataException("Missing EBX after install: " + mapped);
                string key = SourceKey(source.Bundle);
                var target = copies.SingleOrDefault(e => FreshKey(e.Bundle) == key)
                    ?? throw new InvalidDataException($"EBX bundle copy missing: {mapped} from {key}");
                byte[] sourceRaw = Dec(BundleIndex.ReadBytes(source.Data));
                byte[] stored = BundleIndex.ReadBytes(target.Data), raw = Dec(stored);
                var before = EbxRiff.Parse(sourceRaw);
                var after = EbxRiff.Parse(raw);
                if (a.HotspotLayouts.SingleOrDefault(l => l.SourceSlot == source.Name) is { } expectedLayout
                    && !UniformPhysicalLayouts.SameLayout(UniformHotspots.Capture(raw, mapped), expectedLayout))
                    throw new InvalidDataException($"{mapped}: its installed hotspot ids, names, roles or coordinates differ.");
                if (after.Name != mapped || after.FileGuid != Stable("ebx:" + mapped))
                    throw new InvalidDataException("EBX identity is wrong: " + mapped);
                var expectedImports = before.ImportedFileGuids.Select(id => guids.GetValueOrDefault(id, id)).ToArray();
                if (!after.ImportedFileGuids.SequenceEqual(expectedImports))
                    throw new InvalidDataException("EBX imports still point into the source kit: " + mapped);
                foreach (var (oldRid, newRid) in rids)
                {
                    if (sourceRaw.AsSpan().IndexOf(BitConverter.GetBytes(oldRid)) < 0) continue;
                    if (raw.AsSpan().IndexOf(BitConverter.GetBytes(newRid)) < 0 || raw.AsSpan().IndexOf(BitConverter.GetBytes(oldRid)) >= 0)
                        throw new InvalidDataException("EBX resource id was not remapped: " + mapped);
                }
                var manifest = BundleManifest.Parse(BundleIndex.ReadBytes(target.Bundle.Manifest));
                var entry = manifest.Ebx.Single(e => e.Name == mapped);
                if (entry.OriginalSize != raw.Length || !entry.Sha1.AsSpan().SequenceEqual(SHA1.HashData(stored)))
                    throw new InvalidDataException("EBX manifest does not match its payload: " + mapped);
                assets++;
            }

            foreach (var source in sourceRes.Where(r => OwnBundle(r.Bundle)))
            {
                string mapped = Map(source.Name);
                if (!fresh.ResByName.TryGetValue(mapped, out var copies)) throw new InvalidDataException("Missing RES after install: " + mapped);
                string key = SourceKey(source.Bundle);
                var target = copies.SingleOrDefault(r => FreshKey(r.Bundle) == key)
                    ?? throw new InvalidDataException($"RES bundle copy missing: {mapped} from {key}");
                byte[] sourceRaw = Dec(BundleIndex.ReadBytes(source.Data));
                byte[] stored = BundleIndex.ReadBytes(target.Data), raw = Dec(stored);
                if (raw.Length < 116 || BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(112)) != Djb(mapped))
                    throw new InvalidDataException("RES name hash is wrong: " + mapped);
                ulong expectedRid = StableRid(mapped);
                if (target.Rid != expectedRid || target.Rid == source.Rid)
                    throw new InvalidDataException("RES resource id is wrong: " + mapped);
                var before = TextureRes.Parse(sourceRaw);
                var after = TextureRes.Parse(raw);
                if (after.ChunkGuid == before.ChunkGuid) throw new InvalidDataException("Texture still uses the source chunk: " + mapped);
                var manifest = BundleManifest.Parse(BundleIndex.ReadBytes(target.Bundle.Manifest));
                var entry = manifest.Res.Single(r => r.Name == mapped);
                if (entry.Rid != expectedRid || entry.OriginalSize != raw.Length || !entry.Sha1.AsSpan().SequenceEqual(SHA1.HashData(stored)))
                    throw new InvalidDataException("RES manifest does not match its payload: " + mapped);
                if (!manifest.Chunks.Any(c => c.Id == after.ChunkGuid)
                    || manifest.ChunkMeta.AsSpan().IndexOf(BitConverter.GetBytes(Djb(mapped))) < 0)
                    throw new InvalidDataException("Cloned chunk or chunk-metadata name hash is missing: " + mapped);

                long size = after.PayloadSize * Ps4Codec.Slices(after.Header);
                byte[] targetFull = FreshFullChunk(after.ChunkGuid, size);
                if (artHashes.TryGetValue(mapped, out var expectedArt))
                {
                    if (!expectedArt.Contains(Convert.ToHexString(SHA256.HashData(targetFull))))
                        throw new InvalidDataException("Converted texture bytes differ: " + mapped);
                }
                else
                {
                    byte[] sourceFull = Dec(FullChunk(before.ChunkGuid, before.PayloadSize * Ps4Codec.Slices(before.Header)));
                    if (!targetFull.AsSpan().SequenceEqual(sourceFull)) throw new InvalidDataException("Stock texture bytes changed during cloning: " + mapped);
                }
                VerifyChunkReferences(after.ChunkGuid, targetFull, mapped);
                assets++;
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
                if (source.Count != target.Count || target.Count == 0)
                    throw new InvalidDataException($"{kit.TargetFolder}: {target.Count} rows, source has {source.Count}.");
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
        o.Say($"read-back ok: {assets} asset copies, {registered} registered rows (both table versions), "
            + $"{databaseRows} jersey rows, {equipmentRows} equipment rows; resource identities and texture bytes verified");
    }
}
