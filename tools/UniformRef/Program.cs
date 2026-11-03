// UniformRef [season]  (default: the season under way) - the matcher's NHL 27 kit picks checked against nhluniforms.com's drawings.
// Read-only: downloads this and last season's drawings once (cached), decodes the NHL 27 kit colour sheets, writes
// %LOCALAPPDATA%\CenterIceModdingSuite\uniformref\<season>\report.txt.  Never touches a game.
using Suite.Convert27;
using Suite.Core;

var season = args.Length > 0 ? args[0] : UniformReference.CurrentSeason();
var settings = SuiteSettings.Load();
var index = await IndexStore.GetAsync(IndexStore.Nhl27, settings, progress: Console.WriteLine);
var (report, summary) = await UniformReference.CheckAsync(season, settings, new Nhl27Catalog(index), VariantMap.Load(), Console.WriteLine);
Console.WriteLine(summary);
Console.WriteLine("report: " + report);
