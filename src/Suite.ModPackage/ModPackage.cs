using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Suite.ModPackage;

// .cimspkg layout: "CIMSPKG1" | per-file Deflate blobs | manifest JSON (UTF-8) | int64 manifest length | "CIMSPKG1".
// A "patch" entry rewrites byte ranges of a stock file in place and then sets its length (the mod's writers only
// append or overwrite, never shift), so a multi-GB cas costs only the bytes that changed. "add" is a new file,
// "delete" a stock file the mod removed. Every entry carries the stock and modded SHA-256.

public sealed class PackageManifest
{
    public int Format { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Created { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Requires { get; set; } = "";
    public List<PackageFile> Files { get; set; } = new();
}

public sealed class PackageFile
{
    public string Path { get; set; } = "";
    public string Action { get; set; } = "patch";   // patch | add | delete
    public long StockSize { get; set; }
    public string? StockSha256 { get; set; }
    public long ModSize { get; set; }
    public string? ModSha256 { get; set; }
    public long BlobOffset { get; set; }
    public long BlobLength { get; set; }
    public List<long[]> Ranges { get; set; } = new();   // [offset, length] in the modded file, ascending
    [JsonIgnore] public long RangeBytes => Ranges.Sum(r => r[1]);
}

public static class PackageIo
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("CIMSPKG1");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static PackageManifest ReadManifest(string pkgPath)
    {
        using var fs = File.OpenRead(pkgPath);
        return ReadManifest(fs);
    }

    public static PackageManifest ReadManifest(FileStream fs)
    {
        var head = new byte[8];
        fs.ReadExactly(head);
        if (!head.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a CIMS mod package.");
        fs.Seek(-16, SeekOrigin.End);
        var tail = new byte[16];
        fs.ReadExactly(tail);
        if (!tail.AsSpan(8).SequenceEqual(Magic)) throw new InvalidDataException("The mod package is incomplete (truncated download?).");
        long len = BitConverter.ToInt64(tail, 0);
        fs.Seek(-16 - len, SeekOrigin.End);
        var json = new byte[len];
        fs.ReadExactly(json);
        var m = JsonSerializer.Deserialize<PackageManifest>(json, Json) ?? throw new InvalidDataException("Empty manifest.");
        foreach (var f in m.Files) CheckRelative(f.Path);
        return m;
    }

    /// Package paths are relative to the NHL24PC folder; anything that could escape it is refused.
    public static void CheckRelative(string p)
    {
        if (string.IsNullOrWhiteSpace(p) || System.IO.Path.IsPathRooted(p) || p.Contains(':')
            || p.Split('\\', '/').Any(s => s == ".." || s == "."))
            throw new InvalidDataException("Bad path in package: " + p);
    }

    public static string Sha256(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[4 << 20];
        int n;
        while ((n = fs.Read(buf)) > 0) { ct.ThrowIfCancellationRequested(); h.AppendData(buf, 0, n); }
        return Convert.ToHexString(h.GetHashAndReset());
    }

    public static string Size(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):N2} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):N1} MB" : $"{b / 1024.0:N0} KB";
}

public sealed class BuildOptions
{
    public string StockRoot { get; set; } = "";
    public string ModdedRoot { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Name { get; set; } = "Center Ice mod";
    public string Version { get; set; } = "1.0";
    public string Notes { get; set; } = "";
    public string Requires { get; set; } = "NHL24PC V3.2 with NHL 24 1.00 (CUSA37933) and the 1.60 update";
    /// Folders under the roots that are compared. eboot.bin is left out: the NHL24PC setup and launcher patch it per machine.
    public string[] Scopes { get; set; } = { "game", "game-UPDATE" };
    public Func<string, bool> Exclude { get; set; } = DefaultExclude;

    public static bool DefaultExclude(string rel)
    {
        var name = Path.GetFileName(rel);
        return name.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".orig", StringComparison.OrdinalIgnoreCase);
    }
}

public static class PackageBuilder
{
    const int Grain = 4096;          // compare granularity
    const long MergeGap = 64 * 1024; // ranges closer than this are merged

    public static PackageManifest Build(BuildOptions o, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (Path.GetFullPath(o.OutputPath).StartsWith(Path.GetFullPath(o.ModdedRoot).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The package must not be written inside the modded game.");
        var rels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { o.StockRoot, o.ModdedRoot })
            foreach (var scope in o.Scopes)
            {
                var dir = Path.Combine(root, scope);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(root, f);
                    if (!o.Exclude(rel)) rels.Add(rel);
                }
            }
        log?.Report($"Comparing {rels.Count} files...");

        var results = new PackageFile?[rels.Count];
        var list = rels.ToArray();
        int done = 0;
        Parallel.For(0, list.Length, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, i =>
        {
            results[i] = Compare(o.StockRoot, o.ModdedRoot, list[i], ct);
            if (results[i] is { } r) log?.Report($"  changed: {r.Path} ({r.Action}, {PackageIo.Size(r.RangeBytes)})");
            int d = Interlocked.Increment(ref done);
            if (d % 200 == 0) log?.Report($"  {d}/{list.Length} compared");
        });

        var m = new PackageManifest
        {
            Name = o.Name, Version = o.Version, Notes = o.Notes, Requires = o.Requires,
            Created = DateTime.UtcNow.ToString("u"),
            Files = results.Where(r => r is not null).Select(r => r!).ToList(),
        };
        if (m.Files.Count == 0) throw new InvalidOperationException("The modded game is identical to stock; nothing to package.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.OutputPath))!);
        var tmp = o.OutputPath + ".part";
        using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            outFs.Write(PackageIo.Magic);
            var buf = new byte[4 << 20];
            foreach (var f in m.Files)
            {
                ct.ThrowIfCancellationRequested();
                f.BlobOffset = outFs.Position;
                if (f.Action != "delete")
                {
                    log?.Report($"Packing {f.Path} ({PackageIo.Size(f.RangeBytes)})");
                    using var src = new FileStream(Path.Combine(o.ModdedRoot, f.Path), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                    using var z = new DeflateStream(outFs, CompressionLevel.Fastest, leaveOpen: true);
                    foreach (var r in f.Ranges)
                    {
                        src.Position = r[0];
                        long left = r[1];
                        while (left > 0)
                        {
                            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, left));
                            if (n <= 0) throw new EndOfStreamException(f.Path);
                            z.Write(buf, 0, n);
                            left -= n;
                        }
                    }
                }
                f.BlobLength = outFs.Position - f.BlobOffset;
            }
            var json = JsonSerializer.SerializeToUtf8Bytes(m, PackageIo.Json);
            outFs.Write(json);
            outFs.Write(BitConverter.GetBytes((long)json.Length));
            outFs.Write(PackageIo.Magic);
        }
        File.Move(tmp, o.OutputPath, overwrite: true);
        log?.Report($"Package written: {o.OutputPath} ({PackageIo.Size(new FileInfo(o.OutputPath).Length)}, {m.Files.Count} files)");
        return m;
    }

    /// Null when the file is identical in both trees.
    static PackageFile? Compare(string stockRoot, string modRoot, string rel, CancellationToken ct)
    {
        var sp = Path.Combine(stockRoot, rel);
        var mp = Path.Combine(modRoot, rel);
        bool hasS = File.Exists(sp), hasM = File.Exists(mp);
        if (!hasM)
            return new PackageFile { Path = rel, Action = "delete", StockSize = new FileInfo(sp).Length, StockSha256 = PackageIo.Sha256(sp, ct) };
        if (!hasS)
        {
            long len = new FileInfo(mp).Length;
            return new PackageFile { Path = rel, Action = "add", ModSize = len, ModSha256 = PackageIo.Sha256(mp, ct), Ranges = len > 0 ? new() { new[] { 0L, len } } : new() };
        }

        using var s = new FileStream(sp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var m = new FileStream(mp, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        long sLen = s.Length, mLen = m.Length, common = Math.Min(sLen, mLen);
        using var hs = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var hm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var ranges = new List<long[]>();
        void Add(long off, long len)
        {
            if (ranges.Count > 0)
            {
                var last = ranges[^1];
                if (off - (last[0] + last[1]) <= MergeGap) { last[1] = off + len - last[0]; return; }
            }
            ranges.Add(new[] { off, len });
        }

        const int Chunk = 4 << 20;
        var bs = new byte[Chunk]; var bm = new byte[Chunk];
        long pos = 0;
        while (pos < Math.Max(sLen, mLen))
        {
            ct.ThrowIfCancellationRequested();
            int ns = pos < sLen ? ReadFull(s, bs, (int)Math.Min(Chunk, sLen - pos)) : 0;
            int nm = pos < mLen ? ReadFull(m, bm, (int)Math.Min(Chunk, mLen - pos)) : 0;
            if (ns > 0) hs.AppendData(bs, 0, ns);
            if (nm > 0) hm.AppendData(bm, 0, nm);
            int both = (int)Math.Max(0, Math.Min(Math.Min(ns, nm), common - pos));
            if (!bs.AsSpan(0, both).SequenceEqual(bm.AsSpan(0, both)))
                for (int g = 0; g < both; g += Grain)
                {
                    int l = Math.Min(Grain, both - g);
                    if (!bs.AsSpan(g, l).SequenceEqual(bm.AsSpan(g, l))) Add(pos + g, l);
                }
            pos += Math.Max(ns, nm);
        }
        if (mLen > sLen) Add(sLen, mLen - sLen);
        if (ranges.Count == 0 && sLen == mLen) return null;

        long changed = ranges.Sum(r => r[1]);
        if (mLen > 0 && changed > mLen / 2) ranges = new() { new[] { 0L, mLen } };   // mostly rewritten (e.g. a TOC that shifted): ship it whole
        return new PackageFile
        {
            Path = rel, Action = "patch",
            StockSize = sLen, StockSha256 = Convert.ToHexString(hs.GetHashAndReset()),
            ModSize = mLen, ModSha256 = Convert.ToHexString(hm.GetHashAndReset()),
            Ranges = ranges,
        };
    }

    static int ReadFull(Stream st, byte[] b, int want)
    {
        int got = 0;
        while (got < want) { int n = st.Read(b, got, want - got); if (n <= 0) break; got += n; }
        return got;
    }
}

public enum FileState { Stock, Installed, Missing, Different }

public sealed class CheckResult
{
    public List<(PackageFile File, FileState State)> Files { get; } = new();
    public bool AllStock => Files.All(f => f.State == FileState.Stock);
    public bool AllInstalled => Files.All(f => f.State == FileState.Installed);
    public IEnumerable<(PackageFile File, FileState State)> Problems => Files.Where(f => f.State is FileState.Missing or FileState.Different);
}

/// Installs into an NHL24PC folder (the one that holds game\ and game-UPDATE\). Old bytes of every range it
/// overwrites go to user\cims_mod_backup\ first, so uninstall puts the stock files back exactly.
public static class ModInstaller
{
    public const string BackupDirName = @"user\cims_mod_backup";
    static readonly string[] GameProcesses = { "nhl24_new", "nhl24", "launcher" };

    public sealed class BackupState
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Status { get; set; } = "installing";   // installing | installed
        public List<BackupEntry> Entries { get; set; } = new();
    }

    public sealed class BackupEntry
    {
        public string Path { get; set; } = "";
        public string Action { get; set; } = "";
        public long StockSize { get; set; }
        public string? StockSha256 { get; set; }
        public List<long[]> Saved { get; set; } = new();   // [fileOffset, length, dataOffset]
    }

    public static string BackupDir(string root) => Path.Combine(root, BackupDirName);
    static string StatePath(string root) => Path.Combine(BackupDir(root), "state.json");
    static string DataPath(string root) => Path.Combine(BackupDir(root), "data.bin");

    public static BackupState? ReadState(string root)
        => File.Exists(StatePath(root)) ? JsonSerializer.Deserialize<BackupState>(File.ReadAllText(StatePath(root)), PackageIo.Json) : null;

    static void WriteState(string root, BackupState st)
    {
        var p = StatePath(root);
        File.WriteAllText(p + ".tmp", JsonSerializer.Serialize(st, PackageIo.Json));
        File.Move(p + ".tmp", p, overwrite: true);
    }

    public static bool LooksLikeGameRoot(string root)
        => File.Exists(Path.Combine(root, "game", "eboot.bin")) && Directory.Exists(Path.Combine(root, "game", "Data"));

    /// The emulator or launcher running from this NHL24PC folder (another install running elsewhere is fine).
    public static string? GameRunning(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        foreach (var n in GameProcesses)
            foreach (var p in Process.GetProcessesByName(n))
                using (p)
                {
                    string? exe = null;
                    try { exe = p.MainModule?.FileName; } catch { }
                    if (exe is null || exe.StartsWith(full, StringComparison.OrdinalIgnoreCase)) return n + ".exe";
                }
        return null;
    }

    public static CheckResult Check(string root, PackageManifest m, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var res = new CheckResult();
        foreach (var f in m.Files)
        {
            ct.ThrowIfCancellationRequested();
            var p = Path.Combine(root, f.Path);
            FileState st;
            if (!File.Exists(p)) st = f.Action == "add" ? FileState.Stock : f.Action == "delete" ? FileState.Installed : FileState.Missing;
            else
            {
                long len = new FileInfo(p).Length;
                bool maybeStock = f.Action != "add" && len == f.StockSize;
                bool maybeMod = f.Action != "delete" && len == f.ModSize;
                if (!maybeStock && !maybeMod) st = FileState.Different;
                else
                {
                    log?.Report("Checking " + f.Path);
                    var h = PackageIo.Sha256(p, ct);
                    st = maybeStock && h == f.StockSha256 ? FileState.Stock : maybeMod && h == f.ModSha256 ? FileState.Installed : FileState.Different;
                }
            }
            res.Files.Add((f, st));
        }
        return res;
    }

    public static void Install(string root, string pkgPath, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (!LooksLikeGameRoot(root)) throw new InvalidOperationException("This is not an NHL24PC folder (no game\\eboot.bin).");
        if (GameRunning(root) is { } run) throw new InvalidOperationException($"Close the game first ({run} is running).");
        using var pkg = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var m = PackageIo.ReadManifest(pkg);
        if (ReadState(root) is { } old)
            throw new InvalidOperationException(old.Status == "installed"
                ? $"\"{old.Name}\" {old.Version} is already installed. Uninstall it first."
                : "A previous install did not finish. Run Uninstall first to put the stock files back.");

        log?.Report($"Checking your game against {m.Requires}...");
        var chk = Check(root, m, log, ct);
        var bad = chk.Problems.ToList();
        if (bad.Count > 0)
            throw new InvalidOperationException("Your game files don't match the stock version this mod was built for:\n"
                + string.Join("\n", bad.Take(15).Select(b => $"  {b.File.Path}: {(b.State == FileState.Missing ? "missing" : "different")}"))
                + (bad.Count > 15 ? $"\n  ...and {bad.Count - 15} more" : "")
                + "\nThis mod needs: " + m.Requires);
        if (chk.Files.Any(f => f.State == FileState.Installed))
            throw new InvalidOperationException("Some of this mod's files are already in place, but there is no backup to undo them. Reinstall the stock game files first.");

        long backupBytes = m.Files.Where(f => f.Action != "add").Sum(f => SavedRanges(f).Sum(r => Math.Max(0, Math.Min(r[0] + r[1], f.StockSize) - r[0])));
        long growBytes = m.Files.Sum(f => Math.Max(0, f.ModSize - f.StockSize));
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        if (drive.AvailableFreeSpace < backupBytes + growBytes + (256L << 20))
            throw new InvalidOperationException($"Not enough disk space: needs about {PackageIo.Size(backupBytes + growBytes)}, {PackageIo.Size(drive.AvailableFreeSpace)} free.");

        Directory.CreateDirectory(BackupDir(root));
        var state = new BackupState { Name = m.Name, Version = m.Version };
        WriteState(root, state);
        try
        {
            using (var data = new FileStream(DataPath(root), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buf = new byte[4 << 20];
                foreach (var f in m.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = Path.Combine(root, f.Path);
                    log?.Report($"Installing {f.Path}");
                    var e = new BackupEntry { Path = f.Path, Action = f.Action, StockSize = f.StockSize, StockSha256 = f.StockSha256 };
                    if (f.Action != "add")
                    {
                        using var src = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                        var save = SavedRanges(f);
                        foreach (var r in save)
                        {
                            long len = Math.Min(r[0] + r[1], f.StockSize) - r[0];
                            if (len <= 0) continue;
                            e.Saved.Add(new[] { r[0], len, data.Position });
                            src.Position = r[0];
                            CopyN(src, data, len, buf, f.Path);
                        }
                    }
                    data.Flush(true);
                    state.Entries.Add(e);
                    WriteState(root, state);   // recorded before the file is touched, so a rollback covers it

                    if (f.Action == "delete") { File.Delete(p); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                    pkg.Position = f.BlobOffset;
                    using var z = new DeflateStream(new SubStream(pkg, f.BlobOffset, f.BlobLength), CompressionMode.Decompress);
                    using var dst = new FileStream(p, f.Action == "add" ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 20);
                    if (dst.Length < f.ModSize) dst.SetLength(f.ModSize);
                    foreach (var r in f.Ranges) { dst.Position = r[0]; CopyN(z, dst, r[1], buf, f.Path); }
                    dst.SetLength(f.ModSize);
                    dst.Flush(true);
                }
            }

            log?.Report("Verifying installed files...");
            var after = Check(root, m, log, ct);
            var wrong = after.Files.Where(x => x.State != FileState.Installed).ToList();
            if (wrong.Count > 0) throw new IOException("Read-back failed for " + string.Join(", ", wrong.Select(w => w.File.Path)));
            state.Status = "installed";
            WriteState(root, state);
            log?.Report($"Installed \"{m.Name}\" {m.Version}: {m.Files.Count} files. Backup: {BackupDir(root)} ({PackageIo.Size(backupBytes)}).");
        }
        catch (Exception ex)
        {
            log?.Report("Install failed: " + ex.Message + " - putting the stock files back...");
            try { Uninstall(root, log, CancellationToken.None); }
            catch (Exception ex2) { throw new IOException($"Install failed ({ex.Message}) and the rollback also failed ({ex2.Message}). The backup is in {BackupDir(root)}; run Uninstall again.", ex); }
            throw;
        }
    }

    public static void Uninstall(string root, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (GameRunning(root) is { } run) throw new InvalidOperationException($"Close the game first ({run} is running).");
        var state = ReadState(root) ?? throw new InvalidOperationException("No installed mod found in this folder.");
        var buf = new byte[4 << 20];
        var failed = new List<string>();
        using (var data = File.Exists(DataPath(root)) ? new FileStream(DataPath(root), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20) : null)
        {
            for (int i = state.Entries.Count - 1; i >= 0; i--)
            {
                var e = state.Entries[i];
                var p = Path.Combine(root, e.Path);
                log?.Report("Restoring " + e.Path);
                if (e.Action == "add") { if (File.Exists(p)) File.Delete(p); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                using (var dst = new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 20))
                {
                    if (dst.Length < e.StockSize) dst.SetLength(e.StockSize);
                    foreach (var s in e.Saved)
                    {
                        data!.Position = s[2];
                        dst.Position = s[0];
                        CopyN(data, dst, s[1], buf, e.Path);
                    }
                    dst.SetLength(e.StockSize);
                    dst.Flush(true);
                }
                if (e.StockSha256 is not null && PackageIo.Sha256(p, ct) != e.StockSha256) failed.Add(e.Path);
            }
        }
        if (failed.Count > 0) throw new IOException("These files did not come back to stock: " + string.Join(", ", failed) + ". The backup was kept in " + BackupDir(root));
        Directory.Delete(BackupDir(root), recursive: true);
        log?.Report($"Uninstalled \"{state.Name}\" {state.Version}: {state.Entries.Count} files back to stock.");
    }

    /// Stock byte ranges an install overwrites or cuts off: the patched ranges, plus the tail when the mod shortens the file.
    static List<long[]> SavedRanges(PackageFile f)
    {
        if (f.Action == "delete") return new() { new[] { 0L, f.StockSize } };
        var l = new List<long[]>(f.Ranges);
        if (f.ModSize < f.StockSize) l.Add(new[] { f.ModSize, f.StockSize - f.ModSize });
        return l;
    }

    static void CopyN(Stream src, Stream dst, long n, byte[] buf, string what)
    {
        while (n > 0)
        {
            int r = src.Read(buf, 0, (int)Math.Min(buf.Length, n));
            if (r <= 0) throw new EndOfStreamException("Unexpected end of data for " + what);
            dst.Write(buf, 0, r);
            n -= r;
        }
    }
}

/// Read-only window onto part of a stream (one file's blob inside the package).
sealed class SubStream : Stream
{
    readonly Stream _s; readonly long _start, _len; long _pos;
    public SubStream(Stream s, long start, long len) { _s = s; _start = start; _len = len; }
    public override int Read(byte[] buffer, int offset, int count)
    {
        long left = _len - _pos;
        if (left <= 0) return 0;
        _s.Position = _start + _pos;
        int n = _s.Read(buffer, offset, (int)Math.Min(count, left));
        _pos += n;
        return n;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _len;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
