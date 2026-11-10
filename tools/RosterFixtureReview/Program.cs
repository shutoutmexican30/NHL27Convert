using System.Text.Json;
using Suite.Rosters;

string dir = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass the copied fixture directory.");
var db = T3Db.Load(File.ReadAllBytes(Path.Combine(dir, "nhl_game.db")), Path.Combine(dir, "nhl_game-meta.xml"));
var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "tables.json"))).RootElement.EnumerateArray()
    .ToDictionary(e => e.GetProperty("table").GetString()!, e => e.GetProperty("valid").GetInt32(), StringComparer.Ordinal);
int failures = 0;
foreach (string name in new[] { "exhibitionteams", "stockteamequipmenttable" })
{
    var table = db.Tables[name];
    var csv = T3Csv.Load(dir, name);
    Console.WriteLine($"{name}: CSV record slots={csv.Rows.Count}; metadata valid={metadata[name]}; binary in-use={table.InUse}; binary slots={table.Slots}.");
    int common = Math.Min(table.InUse, csv.Rows.Count);
    int compared = 0;
    var differences = new List<string>();
    foreach (var field in table.Fields.Values.OrderBy(f => f.BitOffset))
    {
        if (!csv.Has(field.Name)) continue;
        for (int row = 0; row < common; row++)
        {
            string expected = csv.Get(csv.Rows[row], field.Name);
            string actual = field.Type switch
            {
                0 => db.GetString(table, row, field.Name),
                3 => db.GetInt(table, row, field.Name).ToString(),
                _ => expected,
            };
            compared++;
            if (actual != expected && differences.Count < 30)
                differences.Add($"row {row} {field.Name}: binary '{actual}' != CSV '{expected}' (type {field.Type})");
        }
    }
    Console.WriteLine($"First {common} records: compared {compared} integer/string fields; differences shown={differences.Count}.");
    foreach (string difference in differences) Console.WriteLine("DIFF: " + difference);
    failures += differences.Count;
    if (name == "exhibitionteams")
        for (int row = table.InUse; row < metadata[name]; row++)
            Console.WriteLine($"CSV beyond binary in-use: row={row}; index={csv.Get(csv.Rows[row], "index")}; abbr={csv.Get(csv.Rows[row], "abbrname")}; name={csv.Get(csv.Rows[row], "teamname")}; artid={csv.Get(csv.Rows[row], "artid")}.");
}
Console.WriteLine(failures == 0 ? "FIXTURE REVIEW: common binary/CSV fields match; metadata disagreement remains isolated." : $"FIXTURE REVIEW FAILED: {failures} displayed differences.");
return failures == 0 ? 0 : 2;
