// Snapshot backup  <game> <label> [mod name]  — restore point without writing: TOCs + CAS lengths,
//                                              plus a copy of the mod list inside the backup folder.
// Snapshot restore <game> <backup-dir>        — put the game back to a backup and verify byte for byte.
using Suite.Core;
using Suite.Core.Frostbite;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: Snapshot backup <game> <label> [mod name]  |  Snapshot restore <game> <backup-dir>");
    return 2;
}
var game = Path.GetFullPath(args[1]).TrimEnd(Path.DirectorySeparatorChar);
// Backups only read, so they are safe while the game runs; restores must wait until it is closed.
if (args[0] != "backup" && ApplyTarget.RunningGame() is { } running)
{
    Console.Error.WriteLine(running + " is running. Close the game first.");
    return 1;
}
if (args[0] == "backup")
{
    var label = args.Length > 2 ? args[2] : "snapshot";
    var dir = ApplyTarget.Backup(game);
    File.WriteAllText(Path.Combine(dir, "label.txt"), $"{label}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
    if (args.Length > 3 && ModFile.Load(ModFile.PathFor(args[3])) is { } mod)
        File.Copy(ModFile.PathFor(args[3]), Path.Combine(dir, "mod-" + mod.Name + ".json"));
    Console.WriteLine($"restore point '{label}': {dir}");
    return 0;
}
if (args[0] == "restore")
{
    if (args.Length < 3 || !Directory.Exists(args[2]))
    {
        Console.Error.WriteLine("restore needs the backup folder");
        return 2;
    }
    var backup = Path.GetFullPath(args[2]);
    ApplyTarget.RestoreFromBackup(game, backup);
    var left = ApplyTarget.Compare(game, backup);
    if (left.Count > 0)
    {
        Console.Error.WriteLine("RESTORE INCOMPLETE: " + string.Join("; ", left.Take(10)));
        return 1;
    }
    var labelFile = Path.Combine(backup, "label.txt");
    Console.WriteLine("restored: " + (File.Exists(labelFile) ? File.ReadLines(labelFile).First() : Path.GetFileName(backup)));
    // Bring back the mod list archived with the snapshot, if there is one.
    foreach (var archived in Directory.GetFiles(backup, "mod-*.json"))
    {
        var name = Path.GetFileName(archived)["mod-".Length..^".json".Length];
        File.Copy(archived, ModFile.PathFor(name), true);
        Console.WriteLine($"mod list restored: {name}");
    }
    return 0;
}
Console.Error.WriteLine("unknown verb: " + args[0]);
return 2;
