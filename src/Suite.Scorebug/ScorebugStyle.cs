namespace Suite.Scorebug;

/// <summary>
/// A custom scorebug: its Scaleform movie (written into the game) and its preview movie (the same ScoreClock with its
/// images embedded, for Ruffle). Both ship in the suite's data\scorebug folder, copied from NHL24Kit.
/// </summary>
public sealed record ScorebugStyle(string Id, string Name, string Movie, string Preview)
{
    public static readonly ScorebugStyle[] All =
    [
        new("espn", "ESPN+", "score_clock_espn.gfx", "espn.swf"),
        new("prime", "NHL on Prime Video", "score_clock_prime.gfx", "prime.swf"),
    ];

    public static ScorebugStyle ById(string? id) => All.FirstOrDefault(s => s.Id == id) ?? All[0];

    /// <summary>data\scorebug next to the app (the movies, the preview page, Ruffle).</summary>
    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "data", "scorebug");
    public static string PreviewDir => Path.Combine(DataDir, "preview");

    public string MoviePath => Path.Combine(DataDir, Movie);

    public override string ToString() => Name;
}
