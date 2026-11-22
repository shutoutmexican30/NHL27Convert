// MakeRosterSave <savedataRoot> <name> <db-file> [donor-save-with-sce_sys]
// Creates a roster save folder (what "save roster in game" would make) so the game lists it and the suite can edit it.
using Suite.Rosters;

if (args.Length < 3) { Console.Error.WriteLine("usage: MakeRosterSave <savedataRoot> <name> <db-file> [donor]"); return 2; }
var save = SavedRoster.Create(args[0], args[1], File.ReadAllBytes(args[2]), args.Length > 3 ? args[3] : null);
Console.WriteLine($"created {save.Dir}");
return 0;
