using System.Runtime.InteropServices;

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

    /// <summary>oo2core_9_win64.dll next to the suite's exe, in an FMT install under any drive's \Tools (any version), or next
    /// to the NHL24Kit runtime. The DLL is not redistributed with the suite; a user can drop it beside the exe.</summary>
    public static IEnumerable<string> Candidates(SuiteSettings? s = null)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "oo2core_9_win64.dll");
        if (File.Exists(beside)) yield return beside;
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => Path.Combine(d.RootDirectory.FullName, "Tools"));
        foreach (var root in drives.Append(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> found;
            try
            {
                found = Directory.EnumerateDirectories(root, "FMT*").Concat(Directory.EnumerateDirectories(root, "NHL24Kit"))
                    .SelectMany(d => Directory.EnumerateFiles(d, "oo2core_9_win64.dll", SearchOption.AllDirectories)).ToList();
            }
            catch { continue; }
            foreach (var f in found) yield return f;
        }
        if (s is not null && Directory.Exists(s.WorkerRuntimeDir))
            foreach (var f in Directory.EnumerateFiles(s.WorkerRuntimeDir, "oo2core_9_win64.dll", SearchOption.AllDirectories)) yield return f;
    }

    public static void Ensure(SuiteSettings? s = null)
    {
        if (_decompress is not null && _compress is not null) return;
        lock (InitLock)
        {
            if (_decompress is not null && _compress is not null) return;
            var path = Candidates(s).FirstOrDefault() ?? throw new FileNotFoundException("oo2core_9_win64.dll not found (it ships with FMT).");
            var lib = NativeLibrary.Load(path);
            _decompress = Marshal.GetDelegateForFunctionPointer<DecompressFn>(NativeLibrary.GetExport(lib, "OodleLZ_Decompress"));
            _compress = Marshal.GetDelegateForFunctionPointer<CompressFn>(NativeLibrary.GetExport(lib, "OodleLZ_Compress"));
            LoadedFrom = path;
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
