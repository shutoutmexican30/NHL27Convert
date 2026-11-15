using System.Runtime.InteropServices;
using Suite.ModPackage;

namespace Cims.Installer;

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0) return Cli(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm());
        return 0;
    }

    /// CIMS-Mod-Installer.exe --game <NHL24PC folder> [--package <file.cimspkg>] (--check | --install | --uninstall)
    static int Cli(string[] args)
    {
        AttachConsole(-1);
        string? game = null, pkg = null, cmd = null;
        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--game": game = args[++i]; break;
                case "--package": pkg = args[++i]; break;
                case "--check": case "--install": case "--uninstall": cmd = args[i]; break;
            }
        var syncLog = new SyncLog();
        try
        {
            if (game is null || cmd is null) { Console.WriteLine("usage: --game <NHL24PC folder> [--package <file>] --check|--install|--uninstall"); return 2; }
            pkg ??= InstallerForm.FindPackage();
            switch (cmd)
            {
                case "--check":
                    var m = PackageIo.ReadManifest(pkg ?? throw new FileNotFoundException("No .cimspkg found."));
                    var r = ModInstaller.Check(game, m, syncLog);
                    foreach (var g in r.Files.GroupBy(f => f.State)) Console.WriteLine($"{g.Key}: {g.Count()}");
                    foreach (var p in r.Problems) Console.WriteLine($"  {p.State}: {p.File.Path}");
                    return r.AllStock ? 0 : r.AllInstalled ? 10 : 1;
                case "--install": ModInstaller.Install(game, pkg ?? throw new FileNotFoundException("No .cimspkg found."), syncLog); return 0;
                default: ModInstaller.Uninstall(game, syncLog); return 0;
            }
        }
        catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); return 1; }
    }

    sealed class SyncLog : IProgress<string> { public void Report(string v) => Console.WriteLine(v); }
}
