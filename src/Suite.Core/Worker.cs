namespace Suite.Core;

/// <summary>
/// Stopgap backend: the built NHL24Kit Worker, run as an external tool (never decompiled into this code base).
/// Used for NHL 24 listings and texture dumps until the suite's own reader/writer lands, and later as a
/// byte-for-byte reference for the new writer.
/// </summary>
public static class Worker
{
    public static string Exe(SuiteSettings s) => Path.Combine(s.WorkerRuntimeDir, "NHL24Kit.Worker.exe");

    public static bool Available(SuiteSettings s) =>
        File.Exists(Exe(s)) && File.Exists(Path.Combine(s.WorkerRuntimeDir, "FrostbiteProfiles", "NHL24PS4Profile.json"));

    /// <summary>Runs a read command and returns the JSON after "RESULT ". Write commands are not exposed here.</summary>
    public static async Task<string> RunAsync(SuiteSettings s, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        if (!Available(s)) throw new InvalidOperationException($"NHL24Kit Worker not found in {s.WorkerRuntimeDir}.");
        if (args.Count > 0 && args[0] is "apply" or "restore")
            throw new InvalidOperationException("The suite does not write to the game through the Worker.");
        SuiteLog.Write("worker " + string.Join(' ', args));
        var r = await ProcessRunner.RunAsync(Exe(s), args, s.WorkerRuntimeDir, ct: ct);
        var result = r.StdOut.Split('\n').Select(l => l.TrimEnd('\r').TrimStart('﻿')).LastOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
        if (r.ExitCode != 0 || result is null)
        {
            var err = r.StdOut.Split('\n').FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? r.StdErr;
            throw new InvalidOperationException("Worker failed: " + err.Trim());
        }
        return result["RESULT ".Length..];
    }
}

/// <summary>
/// A folder that looks like one extracted NHL 24 game (package.mft + Data + Patch) built from NHL24PC's
/// game\ and game-UPDATE\ folders. The small initfs/layout files are copies and the big Gen4b folders are
/// junctions, so FMT's start-up backups (initfs *.fmtbak, __FMTBackup) land here, never in the game.
/// Only for reading.
/// </summary>
public static class Nhl24ReadView
{
    public static string Ensure(SuiteSettings s)
    {
        var view = s.Nhl24ReadView;
        var game = s.Nhl24BaseDir;
        var update = s.Nhl24UpdateDir;
        if (!File.Exists(Path.Combine(game, "package.mft")))
            throw new DirectoryNotFoundException($"No NHL 24 base game in {game}.");

        Directory.CreateDirectory(Path.Combine(view, "Data"));
        Directory.CreateDirectory(Path.Combine(view, "Patch"));
        Directory.CreateDirectory(Path.Combine(view, "__FMTBackup"));
        CopyIfChanged(Path.Combine(game, "package.mft"), Path.Combine(view, "package.mft"));
        foreach (var f in new[] { "chunkmanifest", "initfs_Gen4b", "layout.toc" })
            CopyIfChanged(Path.Combine(game, "Data", f), Path.Combine(view, "Data", f));
        Link(Path.Combine(view, "Data", "Gen4b"), Path.Combine(game, "Data", "Gen4b"));
        if (Directory.Exists(Path.Combine(update, "Patch")))
        {
            foreach (var f in new[] { "initfs_Gen4b", "layout.toc" })
                CopyIfChanged(Path.Combine(update, "Patch", f), Path.Combine(view, "Patch", f));
            Link(Path.Combine(view, "Patch", "Gen4b"), Path.Combine(update, "Patch", "Gen4b"));
        }
        // FMT backs up initfs and the whole Patch folder unless these exist; give it nothing to do.
        foreach (var initfs in Directory.GetFiles(view, "initfs_*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".fmtbak")))
            CopyIfChanged(initfs, initfs + ".fmtbak");
        File.WriteAllText(Path.Combine(view, "__FMTBackup", "backup.bin"), "2099-01-01 00:00:00");
        return view;
    }

    static void CopyIfChanged(string src, string dst)
    {
        if (!File.Exists(src)) return;
        var a = new FileInfo(src);
        var b = new FileInfo(dst);
        if (b.Exists && b.Length == a.Length && b.LastWriteTimeUtc == a.LastWriteTimeUtc) return;
        File.Copy(src, dst, true);
    }

    static void Link(string link, string target)
    {
        var info = new DirectoryInfo(link);
        if (info.Exists)
        {
            if (info.LinkTarget is not null && string.Equals(Path.GetFullPath(info.LinkTarget).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return;
            if (info.LinkTarget is null) throw new IOException($"{link} exists and is not a link; refusing to touch it.");
            info.Delete();
        }
        // Directory junctions need no admin rights (symbolic links do).
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(link);
        psi.ArgumentList.Add(target);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        if (!Directory.Exists(link)) throw new IOException($"Could not link {link} -> {target}.");
    }
}
