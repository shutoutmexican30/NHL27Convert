using Suite.ModPackage;

namespace Suite.Tests;

/// Build -> install -> uninstall on tiny fake NHL24PC trees, covering shrink, grow, in-place, add and delete.
public class ModPackageTests
{
    static void Put(string root, string rel, byte[] data) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, data); }
    static byte[] Rnd(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    static Dictionary<string, string> Snap(string root) => Directory.EnumerateFiles(Path.Combine(root, "game"), "*", SearchOption.AllDirectories)
        .Concat(Directory.Exists(Path.Combine(root, "game-UPDATE")) ? Directory.EnumerateFiles(Path.Combine(root, "game-UPDATE"), "*", SearchOption.AllDirectories) : [])
        .ToDictionary(f => Path.GetRelativePath(root, f), f => PackageIo.Sha256(f));

    const string Xml = @"user\patches\nhl24.xml";

    [Fact]
    public void RoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cimspkg-" + Guid.NewGuid().ToString("N"));
        string stock = Path.Combine(dir, "stock"), mod = Path.Combine(dir, "mod"), test = Path.Combine(dir, "test");
        try
        {
            foreach (var r in new[] { stock, mod, test })
            {
                Put(r, @"game\eboot.bin", Rnd(100, 1));
                Put(r, @"game\Data\same.cas", Rnd(300_000, 2));
                Put(r, @"game\Data\toc.toc", Rnd(50_000, 4));
            }
            var big = Rnd(1_000_000, 3);
            Put(stock, @"game\Data\big.cas", big); Put(test, @"game\Data\big.cas", big);
            var bigMod = big.Concat(Rnd(70_000, 5)).ToArray(); bigMod[500_000] ^= 0xFF;   // in-place byte + append
            Put(mod, @"game\Data\big.cas", bigMod);
            Put(stock, @"game-UPDATE\Patch\shrink.cas", Rnd(200_000, 6)); Put(test, @"game-UPDATE\Patch\shrink.cas", Rnd(200_000, 6));
            Put(mod, @"game-UPDATE\Patch\shrink.cas", Rnd(200_000, 6).Take(120_000).ToArray());
            Put(stock, @"game\Data\gone.bin", Rnd(5000, 7)); Put(test, @"game\Data\gone.bin", Rnd(5000, 7));
            Put(mod, @"game\Data\new.bin", Rnd(9000, 8));
            Put(mod, @"game\eboot.bin", Rnd(100, 9));   // setup-managed, must be ignored
            Put(mod, Xml, Rnd(700, 10));   // runtime patch: put whole
            Put(test, Xml, Rnd(300, 11));  // player's own copy, backed up and restored

            var pkg = Path.Combine(dir, "t.cimspkg");
            var m = PackageBuilder.Build(new BuildOptions { StockRoot = stock, ModdedRoot = mod, OutputPath = pkg });
            Assert.Equal(5, m.Files.Count);
            Assert.True(m.Files.Single(f => f.Path.EndsWith("big.cas")).RangeBytes < 200_000);

            var before = Snap(test);
            ModInstaller.Install(test, pkg);
            var modSnap = Snap(mod); modSnap.Remove(@"game\eboot.bin");
            var after = Snap(test); after.Remove(@"game\eboot.bin");
            Assert.Equal(PackageIo.Sha256(Path.Combine(mod, Xml)), PackageIo.Sha256(Path.Combine(test, Xml)));
            Assert.Equal(modSnap.OrderBy(k => k.Key), after.OrderBy(k => k.Key));

            Assert.Throws<InvalidOperationException>(() => ModInstaller.Install(test, pkg));   // already installed
            ModInstaller.Uninstall(test);
            Assert.Equal(before.OrderBy(k => k.Key), Snap(test).OrderBy(k => k.Key));
            Assert.Equal(Rnd(300, 11), File.ReadAllBytes(Path.Combine(test, Xml)));
            Assert.False(Directory.Exists(ModInstaller.BackupDir(test)));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
