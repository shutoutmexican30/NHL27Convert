using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Suite.Core.Frostbite;

/// <summary>
/// Oodle (oo2core_9_win64.dll), loaded from the user's own FMT install at run time; the library is not shipped with
/// the suite. NHL 24 chunk blocks of type 0x11 are Oodle streams.
/// </summary>
public static unsafe class Oodle
{
    delegate long DecompressFn(byte* src, long srcSize, byte* dst, long rawSize, int fuzzSafe, int checkCrc, int verbosity,
        byte* decBufBase, long decBufSize, nint callback, nint callbackData, nint decoderMemory, long decoderMemorySize, int threadPhase);
    delegate long CompressFn(int compressor, byte* src, long srcSize, byte* dst, int level, nint options, nint dictionaryBase,
        nint lrm, nint scratch, long scratchSize);

    static DecompressFn? _decompress;
    static CompressFn? _compress;
    static readonly object InitLock = new();
    public static string? LoadedFrom { get; private set; }

    const int Kraken = 8, LevelOptimal2 = 6;

    const string DllName = "oo2core_9_win64.dll";

    /// <summary>Where the message says to look, kept in sync with Candidates/Ensure.</summary>
    const string MissingMessage =
        DllName + " was not found. The suite needs EA's Oodle compression library to read and write the game's files, and that library is not shipped with the suite.\n" +
        "Fix: copy " + DllName + " — it ships with FMT (Frostbite Mod Tools) and with EA PC games such as Madden, EA SPORTS FC, or EA SPORTS College Football — " +
        "into the suite's folder next to CenterIceModdingSuite.exe, then restart the suite.\n" +
        "Searched: the suite folder, the CIMS_OODLE_DLL environment variable, FMT and NHL24Kit folders under every drive's \\Tools and in AppData, EA/Origin/Epic/Steam game folders, registered game installs, and PATH.";

    /// <summary>oo2core_9_win64.dll next to the suite's exe, the CIMS_OODLE_DLL environment variable, an FMT install under
    /// any drive's \Tools (any version), next to the NHL24Kit runtime, EA game folders (EA Games/Origin/Epic/Steam), or any
    /// registered game's install folder. The DLL is not redistributed with the suite; a user can drop it beside the exe.</summary>
    public static IEnumerable<string> Candidates(SuiteSettings? s = null)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, DllName);
        if (File.Exists(beside)) yield return beside;
        var env = Environment.GetEnvironmentVariable("CIMS_OODLE_DLL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) yield return env;
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => Path.Combine(d.RootDirectory.FullName, "Tools"));
        foreach (var root in drives.Append(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateDirectories(root, "FMT*").Concat(Directory.EnumerateDirectories(root, "NHL24Kit"))
                    .SelectMany(d => Directory.EnumerateFiles(d, DllName, SearchOption.AllDirectories)).ToList();
            }
            catch { continue; }
            foreach (var f in found) yield return f;
        }
        if (s is not null && Directory.Exists(s.WorkerRuntimeDir))
            foreach (var f in Directory.EnumerateFiles(s.WorkerRuntimeDir, DllName, SearchOption.AllDirectories)) yield return f;
        foreach (var root in StoreRoots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> games;
            try
            {
                // Game-store folders: take EA-looking subfolders only, so the depth-limited dll check stays fast.
                var all = Directory.EnumerateDirectories(root).ToList();
                games = root.Contains("steamapps") ? all.Where(IsEaGameName) : all;
            }
            catch { continue; }
            foreach (var f in DllAt(games)) yield return f;
        }
        foreach (var f in DllAt(UninstallLocations())) yield return f;
    }

    static bool IsEaGameName(string path)
    {
        var n = Path.GetFileName(path);
        foreach (var p in new[] { "EA", "Madden", "FIFA", "FC", "College Football", "NHL", "NFL", "F1", "Battlefield", "Apex", "Star Wars", "Dragon Age", "Mass Effect", "Titanfall", "Need for Speed" })
            if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static IEnumerable<string> StoreRoots()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var baseDir in new[] { pf, pf86 }.Where(d => !string.IsNullOrEmpty(d)).Distinct())
            foreach (var store in new[] { "EA Games", "Origin Games", "Epic Games", @"Steam\steamapps\common" })
                yield return Path.Combine(baseDir, store);
    }

    static IEnumerable<string> UninstallLocations()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = TryOpenBaseKey(view);
            if (baseKey is null) continue;
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var key = TryOpenSubKey(baseKey, path);
                if (key is null) continue;
                foreach (var name in TrySubKeyNames(key))
                {
                    using var app = TryOpenSubKey(key, name);
                    if (app?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                        yield return loc;
                }
            }
        }
    }

    static RegistryKey? TryOpenBaseKey(RegistryView view)
    {
        try { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view); } catch { return null; }
    }

    static RegistryKey? TryOpenSubKey(RegistryKey parent, string name)
    {
        try { return parent.OpenSubKey(name); } catch { return null; }
    }

    static string[] TrySubKeyNames(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); } catch { return Array.Empty<string>(); }
    }

    /// <summary>The dll at each root, or one folder deeper (game folders keep it at the root or in a Binaries-style subfolder).</summary>
    static IEnumerable<string> DllAt(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            var direct = Path.Combine(root, DllName);
            if (File.Exists(direct)) { yield return direct; continue; }
            List<string> subs;
            try { subs = Directory.EnumerateDirectories(root).ToList(); }
            catch { continue; }
            foreach (var sub in subs)
            {
                var inner = Path.Combine(sub, DllName);
                if (File.Exists(inner)) yield return inner;
            }
        }
    }

    public static void Ensure(SuiteSettings? s = null)
    {
        if (_decompress is not null && _compress is not null) return;
        lock (InitLock)
        {
            if (_decompress is not null && _compress is not null) return;
            nint lib;
            string from;
            var path = Candidates(s).FirstOrDefault();
            if (path is not null)
            {
                lib = NativeLibrary.Load(path);
                from = path;
            }
            else if (NativeLibrary.TryLoad(DllName, out lib))
            {
                from = "Windows search (exe folder and PATH)";
            }
            else
            {
                throw new FileNotFoundException(MissingMessage);
            }
            _decompress = Marshal.GetDelegateForFunctionPointer<DecompressFn>(NativeLibrary.GetExport(lib, "OodleLZ_Decompress"));
            _compress = Marshal.GetDelegateForFunctionPointer<CompressFn>(NativeLibrary.GetExport(lib, "OodleLZ_Compress"));
            LoadedFrom = from;
        }
    }

    public static byte[] Decompress(ReadOnlySpan<byte> src, int rawSize)
    {
        Ensure();
        var dst = new byte[rawSize];
        long n;
        fixed (byte* s = src) fixed (byte* d = dst)
            n = _decompress!(s, src.Length, d, rawSize, 1, 0, 0, null, 0, 0, 0, 0, 0, 3);
        if (n != rawSize) throw new InvalidDataException($"Oodle decompressed {n} bytes, expected {rawSize}.");
        return dst;
    }

    public static byte[] Compress(ReadOnlySpan<byte> raw)
    {
        Ensure();
        var dst = new byte[raw.Length + 65536 + raw.Length / 8];
        long n;
        fixed (byte* s = raw) fixed (byte* d = dst)
            n = _compress!(Kraken, s, raw.Length, d, LevelOptimal2, 0, 0, 0, 0, 0);
        if (n <= 0) throw new InvalidOperationException("Oodle compression failed.");
        return dst[..(int)n];
    }
}
