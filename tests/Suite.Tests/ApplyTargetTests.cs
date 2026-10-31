using System.Diagnostics;
using Suite.Core;
using Xunit;

namespace Suite.Tests;

public class ApplyTargetTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "cims-applytarget-" + Guid.NewGuid().ToString("N")[..8]);

    string MakeGame(string name)
    {
        var g = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(g, "Data", "Gen4b", "superbundlelayout"));
        Directory.CreateDirectory(Path.Combine(g, "Patch", "Gen4b"));
        File.WriteAllText(Path.Combine(g, "package.mft"), "x");
        File.WriteAllBytes(Path.Combine(g, "Data", "Gen4b", "contentsb.toc"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(g, "Patch", "Gen4b", "contentsb.toc"), [4, 5, 6]);
        File.WriteAllBytes(Path.Combine(g, "Data", "Gen4b", "superbundlelayout", "cas_01.cas"), new byte[100]);
        return g;
    }

    [Fact]
    public void A_complete_real_game_folder_is_accepted()
    {
        var g = MakeGame("ok");
        Assert.Empty(ApplyTarget.Problems(g).Where(p => !p.Contains("is running")));
    }

    [Fact]
    public void Split_or_incomplete_folders_are_refused()
    {
        var g = MakeGame("nopatch");
        Directory.Delete(Path.Combine(g, "Patch"), true);
        Assert.Contains(ApplyTarget.Problems(g), p => p.Contains("Patch"));
        Assert.Contains(ApplyTarget.Problems(Path.Combine(_root, "missing")), p => p.Contains("does not exist"));
    }

    [Fact]
    public void Folders_with_links_into_another_tree_are_refused()
    {
        var real = MakeGame("real");
        var view = Path.Combine(_root, "view");
        Directory.CreateDirectory(Path.Combine(view, "Data"));
        Directory.CreateDirectory(Path.Combine(view, "Patch"));
        File.WriteAllText(Path.Combine(view, "package.mft"), "x");
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "/c", "mklink", "/J", Path.Combine(view, "Data", "Gen4b"), Path.Combine(real, "Data", "Gen4b") }) psi.ArgumentList.Add(a);
        Process.Start(psi)!.WaitForExit();
        Assert.Contains(ApplyTarget.Problems(view), p => p.Contains("Link found"));
    }

    [Fact]
    public void Backup_detects_and_undoes_appends_and_toc_edits()
    {
        var g = MakeGame("roundtrip");
        var backup = ApplyTarget.Backup(g);
        Assert.Empty(ApplyTarget.Compare(g, backup));

        var cas = Path.Combine(g, "Data", "Gen4b", "superbundlelayout", "cas_01.cas");
        using (var fs = new FileStream(cas, FileMode.Append)) fs.Write(new byte[37]);
        File.WriteAllBytes(Path.Combine(g, "Data", "Gen4b", "contentsb.toc"), [9, 9, 9, 9]);
        var diffs = ApplyTarget.Compare(g, backup);
        Assert.Contains(diffs, d => d.StartsWith("CAS length"));
        Assert.Contains(diffs, d => d.StartsWith("TOC changed"));

        ApplyTarget.RestoreFromBackup(g, backup);
        Assert.Empty(ApplyTarget.Compare(g, backup));
        Assert.Equal(100, new FileInfo(cas).Length);
        Directory.Delete(backup, true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
