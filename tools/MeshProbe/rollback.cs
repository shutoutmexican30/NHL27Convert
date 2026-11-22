using System;
using System.IO;
using System.Linq;
using Suite.Core;
using Suite.Core.Frostbite;

var game = @"C:\Users\devin\Downloads\NHL24PC_V3.2\NHL24PC";
var backup = Path.Combine(SuitePaths.Local, "backups", "20261118-021812");

if (ApplyTarget.RunningGame() is { } running)
{
    Console.WriteLine("GAME RUNNING: " + running + " — close it first.");
    return;
}

var problems = ApplyTarget.Problems(game);
if (problems.Count > 0) Console.WriteLine("warnings:\n" + string.Join("\n", problems));

Console.WriteLine("diffs vs 02:18 backup before restore:");
foreach (var d in ApplyTarget.Compare(game, backup).Take(15)) Console.WriteLine("  " + d);

ApplyTarget.RestoreFromBackup(game, backup);
Console.WriteLine("restored from " + backup);

Console.WriteLine("diffs after restore (should be empty):");
var after = ApplyTarget.Compare(game, backup);
Console.WriteLine(after.Count == 0 ? "  none — game matches the 02:18 state" : string.Join("\n", after.Take(15).Select(d => "  " + d)));
