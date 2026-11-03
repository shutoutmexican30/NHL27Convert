using System.Globalization;
using System.Text;
using System.Text.Json;
using Suite.Core;

namespace Suite.Scorebug;

public enum SettingKind { Number, Colour, Flag, Text }

/// <summary>One setting of a scorebug movie. The key and default are the ones the movie's loadConfig uses.</summary>
public sealed record ScorebugSetting(string Group, string Key, string Label, SettingKind Kind, string Default,
    double Min = 0, double Max = 0, double Step = 1, string? Tip = null)
{
    /// <summary>A value cleaned up for this setting (colours as RRGGBB, numbers clamped, flags 0/1, labels upper case ASCII).</summary>
    public string Normalize(string? v, string current)
    {
        v = (v ?? "").Trim();
        switch (Kind)
        {
            case SettingKind.Colour:
                return ScorebugSettings.TryColour(v, out var c) ? c.ToString("X6") : current;
            case SettingKind.Number:
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return current;
                d = Math.Round(Math.Clamp(d, Min, Max) / Step) * Step;
                return Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture);
            case SettingKind.Flag:
                return v is "1" or "true" or "True" ? "1" : "0";
            default:
                var sb = new StringBuilder();
                foreach (var ch in v.ToUpperInvariant())
                    if (ch >= ' ' && ch <= '~' && ch != ';' && ch != '=' && ch != '|') sb.Append(ch);
                return sb.Length > 0 ? sb.ToString() : Default;
        }
    }
}

/// <summary>
/// The settings of one scorebug style: a value per setting (absent = the movie's default) and per-team colour overrides.
/// The settings text is what the movie reads: "key=value;key=value;teamColors=ABBR:RRGGBB:RRGGBB|...", colours as
/// 0xRRGGBB, only changed values written. Schema copied from NHL24Kit's Scorebug tab; <see cref="MissingKeys"/> checks
/// every key against the movie's string pool.
/// </summary>
public sealed class ScorebugSettings
{
    public ScorebugStyle Style { get; }
    public IReadOnlyList<ScorebugSetting> All { get; }
    readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    public List<(string Abbr, string Primary, string Secondary)> TeamColours { get; } = [];

    public ScorebugSettings(ScorebugStyle style)
    {
        Style = style;
        All = style.Id == "prime" ? Prime : Espn;
    }

    public string Get(ScorebugSetting s) => _values.TryGetValue(s.Key, out var v) ? v : s.Default;
    public bool IsDefault(ScorebugSetting s) => !_values.ContainsKey(s.Key);

    /// <summary>Sets a value (normalized); returns true when it changed.</summary>
    public bool Set(ScorebugSetting s, string? value)
    {
        var now = Get(s);
        var v = s.Normalize(value, now);
        if (v == now) return false;
        if (v == s.Default) _values.Remove(s.Key); else _values[s.Key] = v;
        return true;
    }

    public void ResetAll() { _values.Clear(); TeamColours.Clear(); }

    public string ToSettingsString()
    {
        var parts = All.Where(s => !IsDefault(s)).Select(s => s.Key + "=" + (s.Kind == SettingKind.Colour ? "0x" + Get(s) : Get(s))).ToList();
        var teams = TeamColours.Where(t => t.Abbr.Length > 0).Select(t => $"{t.Abbr}:{t.Primary}:{t.Secondary}").ToList();
        if (teams.Count > 0) parts.Add("teamColors=" + string.Join("|", teams));
        return string.Join(";", parts);
    }

    /// <summary>Reads a settings text (as written in a movie) back into values; unknown keys are ignored.</summary>
    public void FromSettingsString(string text)
    {
        ResetAll();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..];
            if (key == "teamColors")
            {
                foreach (var t in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    if (t.Split(':') is [var a, var p, var s] && TryColour(p, out var pc) && TryColour(s, out var sc))
                        TeamColours.Add((CleanAbbr(a), pc.ToString("X6"), sc.ToString("X6")));
                continue;
            }
            if (All.FirstOrDefault(s => s.Key == key) is { } setting) Set(setting, value);
        }
    }

    public static string CleanAbbr(string? s) => new((s ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).Take(4).ToArray());

    public static bool TryColour(string? text, out uint colour)
    {
        var t = (text ?? "").Trim().TrimStart('#');
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        colour = 0;
        return t.Length is > 0 and <= 6 && uint.TryParse(t, NumberStyles.HexNumber, null, out colour);
    }

    /// <summary>The pack/store shape (NHL24Kit's: Style, Values of changed settings, TeamColours as [abbr, primary, secondary]).</summary>
    public sealed class Stored
    {
        public string? Style { get; set; }
        public Dictionary<string, string> Values { get; set; } = [];
        public List<string[]> TeamColours { get; set; } = [];
    }

    public Stored ToStored() => new()
    {
        Style = Style.Id,
        Values = All.Where(s => !IsDefault(s)).ToDictionary(s => s.Key, Get),
        TeamColours = TeamColours.Where(t => t.Abbr.Length > 0).Select(t => new[] { t.Abbr, t.Primary, t.Secondary }).ToList(),
    };

    public void FromStored(Stored? st)
    {
        ResetAll();
        foreach (var s in All)
            if (st?.Values is { } v && v.TryGetValue(s.Key, out var value)) Set(s, value);
        foreach (var t in st?.TeamColours ?? [])
            if (t.Length >= 3 && TryColour(t[1], out var p) && TryColour(t[2], out var c))
                TeamColours.Add((CleanAbbr(t[0]), p.ToString("X6"), c.ToString("X6")));
    }

    /// <summary>Schema keys the style's movie does not contain (should be none).</summary>
    public static List<string> MissingKeys(ScorebugStyle style, byte[] movie)
    {
        var pool = ScorebugMovie.PoolStrings(movie);
        return new ScorebugSettings(style).All.Select(s => s.Key).Append("teamColors").Where(k => !pool.Contains(k)).ToList();
    }

    // What the user last chose: %APPDATA%\CenterIceModdingSuite\scorebug\style.txt and <style>.json.
    public static string StoreDir => SuitePaths.Ensure(Path.Combine(SuitePaths.Roaming, "scorebug"));
    static string StyleFile => Path.Combine(StoreDir, "style.txt");
    string StoreFile => Path.Combine(StoreDir, Style.Id + ".json");

    public static ScorebugStyle LoadActiveStyle()
    {
        try { return File.Exists(StyleFile) ? ScorebugStyle.ById(File.ReadAllText(StyleFile).Trim()) : ScorebugStyle.All[0]; }
        catch { return ScorebugStyle.All[0]; }
    }

    public static void SaveActiveStyle(ScorebugStyle style) => File.WriteAllText(StyleFile, style.Id);

    public void Save() => File.WriteAllText(StoreFile, JsonSerializer.Serialize(ToStored(), new JsonSerializerOptions { WriteIndented = true }));

    public bool Load()
    {
        if (!File.Exists(StoreFile)) return false;
        try { FromStored(JsonSerializer.Deserialize<Stored>(File.ReadAllText(StoreFile))); return true; }
        catch { return false; }
    }

    static ScorebugSetting N(string g, string k, string l, double d, double min, double max, double step = 1, string? tip = null) =>
        new(g, k, l, SettingKind.Number, d.ToString("0.###", CultureInfo.InvariantCulture), min, max, step, tip);
    static ScorebugSetting C(string g, string k, string l, string d, string? tip = null) => new(g, k, l, SettingKind.Colour, d, Tip: tip);
    static ScorebugSetting F(string g, string k, string l, bool d, string? tip = null) => new(g, k, l, SettingKind.Flag, d ? "1" : "0", Tip: tip);
    static ScorebugSetting T(string g, string k, string l, string d, string? tip = null) => new(g, k, l, SettingKind.Text, d, Tip: tip);

    static readonly ScorebugSetting[] Espn =
    [
        N("Position and size", "x", "Left edge (px)", 56, 0, 1600, 1, "Distance from the left of a 1920x1080 screen."),
        N("Position and size", "y", "Top edge (px)", 40, 0, 900, 1, "Distance from the top of a 1920x1080 screen."),
        N("Position and size", "scale", "Scale", 1, 0.5, 2, 0.05, "Size of the whole scorebug."),
        N("Layout", "rowH", "Row height", 50, 30, 90),
        N("Layout", "leagueW", "League block width", 76, 40, 160, 1, "The NHL shield and ESPN+ block."),
        N("Layout", "clockW", "Clock block width", 100, 60, 200),
        N("Layout", "teamW", "Team row width", 232, 150, 400),
        N("Layout", "shotsW", "Shots column width", 78, 50, 150),
        N("Layout", "barH", "Bottom bar height", 40, 24, 70, 1, "The power play / message bar."),
        N("Layout", "logoSize", "Row logo size", 96, 40, 180),
        N("Layout", "scoreInset", "Score column inset", 36, 15, 80, 1, "Distance from the row's right edge to the centre of the score."),
        F("Layout", "showShield", "Show NHL shield", true),
        F("Layout", "showEspn", "Show ESPN+ wordmark", true),
        F("Layout", "showRowLine", "Line between the rows", true),
        C("Colours", "cLeague", "League block", "131313"),
        C("Colours", "cClock", "Clock block", "3A3A3A"),
        C("Colours", "cClockText", "Period and clock text", "EDEDED"),
        C("Colours", "cDivider", "Clock divider", "8E8E8E"),
        C("Colours", "cText", "Team text", "FFFFFF"),
        C("Colours", "cHighlight", "Power play highlight", "F5CF2D", "The power play team's name and other highlighted text."),
        C("Colours", "cRowLine", "Row line", "DCDCDC"),
        C("Colours", "cBar", "Bottom bar", "111111", "Messages, and power plays when the team colour is off."),
        C("Colours", "cSide", "Neutral side panel", "1F3E96", "Side panel messages that belong to neither team."),
        N("Text sizes", "sPeriod", "Period", 29, 12, 60),
        N("Text sizes", "sPeriodSuffix", "Period suffix (ST, ND...)", 21, 8, 50),
        N("Text sizes", "sClock", "Clock", 32, 12, 60),
        N("Text sizes", "sAbbr", "Team abbreviation", 33, 12, 60),
        N("Text sizes", "sScore", "Score", 50, 16, 80),
        N("Text sizes", "sBar", "Bottom bar", 27, 12, 50),
        N("Text sizes", "sSide", "Side panel", 19, 10, 40),
        N("Text sizes", "sShots", "Shots numbers", 32, 12, 60),
        N("Text sizes", "sShotsLabel", "Shots label", 14, 8, 30),
        F("Team rows", "rowGradient", "Gradient (secondary to primary)", true),
        N("Team rows", "gradA", "Gradient start (0-255)", 70, 0, 255),
        N("Team rows", "gradB", "Gradient end (0-255)", 175, 0, 255),
        F("Team rows", "darkenLight", "Darken very light team colours", true, "Keeps white text readable on light team colours."),
        F("Power play", "ppTeamColour", "Bar in the team's colour", true),
        F("Power play", "ppLogo", "Faded team logo in the bar", true),
        N("Power play", "ppLogoSize", "Logo size", 72, 30, 160),
        N("Power play", "ppLogoAlpha", "Logo opacity", 0.28, 0, 1, 0.01),
        F("Power play", "ppHighlightName", "Highlight the team's name", true),
        F("Goal animation", "goalBanner", "Goal banner", true, "Off: the scoring row flashes instead."),
        C("Goal animation", "cGoalText", "Banner text", "DDE1E5"),
        N("Goal animation", "sGoal", "Banner text size", 112, 40, 160),
        N("Goal animation", "goalW", "Banner width", 1010, 500, 1800),
        N("Goal animation", "goalPanelW", "Team panel width", 330, 150, 600),
        N("Goal animation", "goalLogo", "Banner logo size", 250, 100, 400),
        N("Goal animation", "goalLetterX", "First letter position", 432, 200, 1200),
        N("Goal animation", "goalLetterStep", "Letter spacing", 132, 60, 250),
        N("Goal animation", "goalMinHold", "Shortest time on screen (s)", 4.5, 1, 15, 0.5),
        N("Goal animation", "goalMaxHold", "Longest time on screen (s)", 6, 2, 20, 0.5),
        T("Labels", "lPowerPlay", "Power play", "POWERPLAY"),
        T("Labels", "lGoal", "Goal banner", "GOAL"),
        T("Labels", "lShootout", "Shootout", "SHOOTOUT"),
        N("Timers", "sideHold", "Side panel (s)", 5, 1, 20, 0.5, "How long goal and player info stays up."),
        N("Timers", "shotsHold", "Shots comparison (s)", 4, 1, 20, 0.5),
    ];

    static readonly ScorebugSetting[] Prime =
    [
        N("Position and size", "x", "Left edge (px)", 44, 0, 1600, 1, "Distance from the left of a 1920x1080 screen."),
        N("Position and size", "y", "Top edge (px)", 24, 0, 900, 1, "Distance from the top of a 1920x1080 screen."),
        N("Position and size", "scale", "Scale", 1, 0.5, 2, 0.05, "Size of the whole scorebug."),
        F("Position and size", "showWatermark", "NHL | prime corner logo", true, "The watermark in the top-right corner."),
        N("Layout", "rowH", "Row height", 50, 30, 90),
        N("Layout", "clockW", "Clock block width", 162, 110, 260, 1, "To the centre of the puck."),
        N("Layout", "puckR", "Puck size", 57, 35, 90),
        N("Layout", "teamW", "Team row width", 215, 150, 360, 1, "From the centre of the puck to the score."),
        N("Layout", "scoreW", "Score cell width", 65, 40, 120),
        F("Layout", "showShots", "Shots column", false, "The game only sends shots now and then (at stoppages), so the count lags."),
        N("Layout", "shotsW", "Shots column width", 63, 40, 120),
        N("Layout", "logoSize", "Row logo size", 54, 30, 100),
        N("Layout", "endCurve", "Curve of the ends", 14, 0, 30),
        F("Layout", "rowGloss", "Sheen on the team rows", true),
        F("Layout", "darkenLight", "Darken very light team colours", true, "Keeps white text readable on light team colours."),
        C("Colours", "cClock", "Clock block", "0D0E11"),
        C("Colours", "cClockText", "Period and clock text", "FFFFFF"),
        C("Colours", "cDivider", "Clock divider", "3A6BEB"),
        C("Colours", "cPuck", "Puck", "0B0C0F"),
        C("Colours", "cRing", "Puck ring", "2D67F5"),
        C("Colours", "cRim", "Puck rim", "B9C0CB"),
        C("Colours", "cShots", "Shots column", "2A2B2E"),
        C("Colours", "cText", "Team text", "FFFFFF"),
        C("Colours", "cNeutral", "Neutral side panel", "1C47C9", "Messages that belong to neither team (empty net...)."),
        N("Text sizes", "sPeriod", "Period", 23, 12, 50),
        N("Text sizes", "sClock", "Clock", 31, 12, 60),
        N("Text sizes", "sAbbr", "Team abbreviation", 30, 12, 60),
        N("Text sizes", "sScore", "Score", 48, 16, 80),
        N("Text sizes", "sShots", "Shots numbers", 25, 12, 50),
        N("Text sizes", "sShotsLabel", "Shots label", 12, 8, 30),
        N("Text sizes", "sSide", "Side panel", 27, 10, 40),
        F("Power play", "ppRing", "Countdown ring on the puck", true),
        F("Power play", "greyShorthanded", "Grey out the shorthanded team", true),
        C("Power play", "cShorthanded", "Shorthanded row", "8E8F92"),
        N("Power play", "sPPLabel", "Label size", 13, 8, 30),
        N("Power play", "sPPTime", "Time size", 27, 12, 50),
        F("Goal animation", "goalBanner", "Goal banner", true, "Off: only the puck flips."),
        C("Goal animation", "cPrimeBlue", "Puck colour on a goal", "1A8CFF"),
        N("Goal animation", "sGoal", "Banner text size", 96, 40, 150),
        N("Goal animation", "goalW", "Banner width", 600, 350, 1200),
        N("Goal animation", "goalMinHold", "Shortest time on screen (s)", 2.5, 1, 15, 0.5),
        N("Goal animation", "goalMaxHold", "Longest time on screen (s)", 4, 2, 20, 0.5),
        F("Intro badge", "introBadge", "Show the badge when the game starts", true),
        T("Intro badge", "lShow1", "Line 1", "MONDAY"),
        T("Intro badge", "lShow2", "Line 2", "NIGHT"),
        T("Intro badge", "lShow3", "Line 3", "HOCKEY"),
        N("Intro badge", "introHold", "Time on screen (s)", 1.4, 0.5, 6, 0.1),
        T("Labels", "lPowerPlay", "Power play", "POWER PLAY"),
        T("Labels", "lGoal", "Goal banner", "GOAL"),
        T("Labels", "lShots", "Shots", "SHOTS"),
        N("Timers", "sideHold", "Side panel (s)", 5, 1, 20, 0.5, "How long goal and player info stays up."),
        N("Timers", "shotsHold", "Other stats (s)", 4, 1, 20, 0.5, "How long another stat replaces shots."),
    ];
}
