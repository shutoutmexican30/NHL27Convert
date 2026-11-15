using Suite.ModPackage;

var o = new BuildOptions();
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--stock": o.StockRoot = args[++i]; break;
        case "--modded": o.ModdedRoot = args[++i]; break;
        case "--out": o.OutputPath = args[++i]; break;
        case "--name": o.Name = args[++i]; break;
        case "--version": o.Version = args[++i]; break;
        case "--notes": o.Notes = args[++i]; break;
    }
if (o.StockRoot == "" || o.ModdedRoot == "" || o.OutputPath == "")
{
    Console.WriteLine("usage: ModPackageBuild --stock <fresh NHL24PC> --modded <modded NHL24PC> --out <file.cimspkg> [--name N] [--version V] [--notes T]");
    return 2;
}
var sw = System.Diagnostics.Stopwatch.StartNew();
var m = PackageBuilder.Build(o, new SyncLog());
Console.WriteLine($"{m.Files.Count} files, {PackageIo.Size(m.Files.Sum(f => f.RangeBytes))} of changed bytes, {sw.Elapsed:mm\\:ss}");
foreach (var f in m.Files) Console.WriteLine($"  {f.Action,-6} {f.Path}  stock {f.StockSize} -> mod {f.ModSize}, {f.Ranges.Count} ranges, {PackageIo.Size(f.RangeBytes)}");
return 0;

sealed class SyncLog : IProgress<string> { public void Report(string v) => Console.WriteLine(v); }
