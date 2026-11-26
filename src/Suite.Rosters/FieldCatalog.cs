using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Suite.Rosters;

/// <summary>How a field is shown and edited.</summary>
public enum FieldKind { Text, Number, Rating, Flag, Choice, Colour, Year, Month, Day, Height, Weight, Money, Team }

/// <summary>A field's place in the studio: its group, plain label, kind, the named list it picks from (<see cref="ChoiceSets"/>) and a hint.</summary>
public sealed record FieldSpec(string Group, string Label, FieldKind Kind = FieldKind.Number, string? Choices = null, string? Hint = null);

/// <summary>
/// Field catalogue of the Database studio: for the player tables (bio, skater and goalie ratings, gear, roster rows) and the team table, which group
/// a field belongs to, its name in plain words, and how its value is edited. The player tables follow Blueline Studio's roster field map
/// (MIT, github.com/SonkuModz/Blueline-Studio, docs/ROSTER_EDITOR.md) after checking every encoding against NHL 24's own database; fields the
/// catalogue does not name are still shown, placed and typed by <see cref="Guess"/>, so nothing is hidden.
/// Values here are what <see cref="T3Db.GetInt"/> returns (the stored value plus the field's range low), so "no team" is -1 and a year is
/// the year minus 1900.
/// </summary>
public static class FieldCatalog
{
    public const string NameGroup = "Name and number", BirthGroup = "Birth and body", ClubGroup = "Club", ContractGroup = "Contract",
        DraftGroup = "Draft and career", CategoryGroup = "Overall and categories", RatingGroup = "Ratings", StyleGroup = "Potential and style",
        TraitGroup = "Traits", TendencyGroup = "Tendencies and presentation", AppearanceGroup = "Appearance", GearGroup = "Gear",
        GearColourGroup = "Gear colours", LinesGroup = "Teams and lines", PersonalityGroup = "Personality and morale (Franchise)", OtherGroup = "Other",
        TeamNames = "Names", TeamIdentity = "League and club", TeamRatings = "Ratings", TeamColours = "Colours", TeamMasks = "Goalie masks",
        TeamFranchise = "Franchise", TeamPresentation = "Arena and presentation";

    public static readonly IReadOnlyList<string> PlayerGroups =
    [
        NameGroup, BirthGroup, ClubGroup, ContractGroup, DraftGroup, CategoryGroup, RatingGroup, StyleGroup, TraitGroup, TendencyGroup,
        AppearanceGroup, GearGroup, GearColourGroup, LinesGroup, PersonalityGroup, OtherGroup,
    ];

    public static readonly IReadOnlyList<string> TeamGroups = [TeamNames, TeamIdentity, TeamRatings, TeamColours, TeamMasks, TeamFranchise, TeamPresentation, OtherGroup];

    const string RatingHint = "0 to 99.";

    static FieldSpec Rating(string group, string label, string? hint = null) => new(group, label, FieldKind.Rating, Hint: hint);

    public static readonly IReadOnlyDictionary<string, FieldSpec> Bio = new Dictionary<string, FieldSpec>(StringComparer.Ordinal)
    {
        ["firstname"] = new(NameGroup, "First name", FieldKind.Text),
        ["lastname"] = new(NameGroup, "Last name", FieldKind.Text),
        ["nickname"] = new(NameGroup, "Nickname", FieldKind.Text),
        ["jersey"] = new(NameGroup, "Jersey number", Hint: "His preferred number. Each team he is on keeps its own number too (Teams and lines)."),
        ["position"] = new(NameGroup, "Position", FieldKind.Choice, ChoiceSets.Position),
        ["positionprimary"] = new(NameGroup, "Position (left/right)", FieldKind.Choice, ChoiceSets.PositionPrimary),
        ["positionsecondary"] = new(NameGroup, "Second position", FieldKind.Choice, ChoiceSets.PositionPrimary),
        ["handedness"] = new(NameGroup, "Shoots / catches", FieldKind.Choice, ChoiceSets.Hand),
        ["audioid"] = new(NameGroup, "Commentary id", Hint: "Which commentary recording says his name."),
        ["surnameid"] = new(NameGroup, "Surname recording id", Hint: "-1 = none."),
        ["year"] = new(BirthGroup, "Birth year", FieldKind.Year),
        ["month"] = new(BirthGroup, "Birth month", FieldKind.Month),
        ["day"] = new(BirthGroup, "Birth day", FieldKind.Day),
        ["birthplace"] = new(BirthGroup, "Birth city", FieldKind.Text),
        ["state"] = new(BirthGroup, "Born in", FieldKind.Choice, ChoiceSets.Origin, "Province, state or country."),
        ["intlcountry"] = new(BirthGroup, "Nationality", FieldKind.Choice, ChoiceSets.Origin),
        ["height"] = new(BirthGroup, "Height", FieldKind.Height),
        ["weight"] = new(BirthGroup, "Weight", FieldKind.Weight),
        ["team"] = new(ClubGroup, "Team", FieldKind.Team, Hint: "His current club. To move a player, use the lines page: it also moves his roster row and lines."),
        ["proteam"] = new(ClubGroup, "Pro team (organization)", FieldKind.Team),
        ["active"] = new(ClubGroup, "Active", Hint: "Retired players are 0."),
        ["islegend"] = new(ClubGroup, "Legend"),
        ["rookie"] = new(ClubGroup, "NHL rookie"),
        ["rookieahl"] = new(ClubGroup, "AHL rookie"),
        ["rookiechl"] = new(ClubGroup, "Junior rookie"),
        ["contractdollars"] = new(ContractGroup, "Cap hit", FieldKind.Money, Hint: "Stored in $5,000 steps."),
        ["contractlength"] = new(ContractGroup, "Years left"),
        ["contractis2way"] = new(ContractGroup, "Contract type", FieldKind.Choice, ChoiceSets.ContractType),
        ["contractismultiyear"] = new(ContractGroup, "Multi-year"),
        ["contractstatus"] = new(ContractGroup, "Contract status"),
        ["contractneedsentrylevel"] = new(ContractGroup, "Needs entry-level deal"),
        ["extensiondollars"] = new(ContractGroup, "Extension cap hit", FieldKind.Money),
        ["extensionlength"] = new(ContractGroup, "Extension years"),
        ["extensionis2way"] = new(ContractGroup, "Extension type", FieldKind.Choice, ChoiceSets.ContractType),
        ["retainedsalarytrades"] = new(ContractGroup, "Salary-retained trades"),
        ["yearufa"] = new(ContractGroup, "UFA year", FieldKind.Year),
        ["yearwaivereligible"] = new(ContractGroup, "Waiver-eligible year", FieldKind.Year),
        ["yearfirstsigned"] = new(ContractGroup, "First signed", FieldKind.Year),
        ["draftteam"] = new(DraftGroup, "Drafted by", FieldKind.Team),
        ["draftyear"] = new(DraftGroup, "Draft year", FieldKind.Year),
        ["draftround"] = new(DraftGroup, "Draft round"),
        ["draftposition"] = new(DraftGroup, "Pick overall"),
        ["isdraftable"] = new(DraftGroup, "In the draft pool"),
        ["yearprodebut"] = new(DraftGroup, "Pro debut", FieldKind.Year),
        ["juniorteam"] = new(DraftGroup, "Junior team", FieldKind.Team),
        ["juniorleague"] = new(DraftGroup, "Junior league"),
        ["cupswon"] = new(DraftGroup, "Cups won"),
        ["cupsyearlastwon"] = new(DraftGroup, "Cup last won", FieldKind.Year),
        ["artid"] = new(AppearanceGroup, "Portrait / art id", Hint: "Picks the menu portrait playerportraits/p<art id>."),
        ["hasportrait"] = new(AppearanceGroup, "Has a portrait"),
        ["headid"] = new(AppearanceGroup, "Head model"),
        ["headtexture"] = new(AppearanceGroup, "Head texture"),
        ["skincolor"] = new(AppearanceGroup, "Skin tone"),
        ["hairid"] = new(AppearanceGroup, "Hair style", FieldKind.Choice, ChoiceSets.HairStyle),
        ["haircolorcode"] = new(AppearanceGroup, "Hair colour", FieldKind.Choice, ChoiceSets.HairColour),
        ["eyecolorcode"] = new(AppearanceGroup, "Eye colour", FieldKind.Choice, ChoiceSets.EyeColour),
        ["eyebrowid"] = new(AppearanceGroup, "Eyebrows"),
        ["eyebrowcolorcode"] = new(AppearanceGroup, "Eyebrow colour"),
        ["playoffbeardtype"] = new(AppearanceGroup, "Beard style"),
        ["playoffbeardcolor"] = new(AppearanceGroup, "Beard colour"),
        ["playoffbeardstart"] = new(AppearanceGroup, "Beard length now"),
        ["playoffbeardmax"] = new(AppearanceGroup, "Playoff beard length"),
        ["playoffbeardgrowthrate"] = new(AppearanceGroup, "Beard growth rate"),
        ["introsongid"] = new(TendencyGroup, "Intro song", Hint: "-1 = none."),
    };

    public static readonly IReadOnlyDictionary<string, FieldSpec> Skater = new Dictionary<string, FieldSpec>(StringComparer.Ordinal)
    {
        ["skill_overall"] = Rating(CategoryGroup, "Overall", "The overall the menus show."),
        ["skill_skate"] = Rating(CategoryGroup, "Skating"), ["skill_shoot"] = Rating(CategoryGroup, "Shooting"), ["skill_puck"] = Rating(CategoryGroup, "Puck skills"),
        ["skill_sense"] = Rating(CategoryGroup, "Senses"), ["skill_defense"] = Rating(CategoryGroup, "Defence"), ["skill_physical"] = Rating(CategoryGroup, "Physical"),
        ["c_speed"] = Rating(RatingGroup, "Speed", RatingHint), ["c_acceleration"] = Rating(RatingGroup, "Acceleration"), ["c_agility"] = Rating(RatingGroup, "Agility"),
        ["c_balance"] = Rating(RatingGroup, "Balance"), ["c_endurance"] = Rating(RatingGroup, "Endurance"), ["c_durability"] = Rating(RatingGroup, "Durability"),
        ["c_puckcontrol"] = Rating(RatingGroup, "Puck control"), ["c_deking"] = Rating(RatingGroup, "Deking"), ["c_handeye"] = Rating(RatingGroup, "Hand-eye"),
        ["c_passing"] = Rating(RatingGroup, "Passing"), ["c_poise"] = Rating(RatingGroup, "Poise"),
        ["c_wristshotpower"] = Rating(RatingGroup, "Wrist shot power"), ["c_wristaccuracy"] = Rating(RatingGroup, "Wrist shot accuracy"),
        ["c_slapshotpower"] = Rating(RatingGroup, "Slap shot power"), ["c_slapaccuracy"] = Rating(RatingGroup, "Slap shot accuracy"),
        ["c_offawareness"] = Rating(RatingGroup, "Offensive awareness"), ["c_defawareness"] = Rating(RatingGroup, "Defensive awareness"),
        ["c_shotblocking"] = Rating(RatingGroup, "Shot blocking"), ["c_stickchecking"] = Rating(RatingGroup, "Stick checking"), ["c_bodychecking"] = Rating(RatingGroup, "Body checking"),
        ["c_strength"] = Rating(RatingGroup, "Strength"), ["c_faceoffs"] = Rating(RatingGroup, "Faceoffs"), ["c_aggression"] = Rating(RatingGroup, "Aggressiveness"),
        ["c_discipline"] = Rating(RatingGroup, "Discipline"), ["c_fightingskill"] = Rating(RatingGroup, "Fighting skill"),
        ["c_fighting"] = new(StyleGroup, "Fights", FieldKind.Choice, ChoiceSets.Fighting),
        ["playerstyle"] = new(StyleGroup, "Archetype", FieldKind.Choice, ChoiceSets.SkaterStyle, "Each team he is on keeps a copy on its roster row."),
        ["growthtier"] = new(StyleGroup, "Potential tier"), ["growthletter"] = new(StyleGroup, "Potential likelihood"), ["growthspeed"] = new(StyleGroup, "Development speed"),
        ["growthage"] = new(StyleGroup, "Development age"), ["growthmod"] = new(StyleGroup, "Development modifier"),
        ["c_overall"] = new(StyleGroup, "c_overall", Hint: "Not the overall the menus show (that is Overall above)."), ["c_potential"] = new(StyleGroup, "c_potential"),
        ["xza"] = new(StyleGroup, "X-Factor (zone)", Hint: "0 = none. NHL 24's own X-Factor ids."), ["xsa1"] = new(StyleGroup, "X-Factor specialist 1"),
        ["xsa2"] = new(StyleGroup, "X-Factor specialist 2"), ["xsa3"] = new(StyleGroup, "X-Factor specialist 3"), ["xsa4"] = new(StyleGroup, "X-Factor specialist 4"), ["xsa5"] = new(StyleGroup, "X-Factor specialist 5"),
        ["off_defbias"] = new(TendencyGroup, "Offence / defence bias"), ["pass_carrybias"] = new(TendencyGroup, "Pass / carry bias"), ["shoot_passbias"] = new(TendencyGroup, "Shoot / pass bias"),
        ["skatingstyle"] = new(TendencyGroup, "Skating style", FieldKind.Choice, ChoiceSets.SkatingStyle),
        ["goalcelebration"] = new(TendencyGroup, "Goal celebration", FieldKind.Choice, ChoiceSets.Celebration), ["goalsongid"] = new(TendencyGroup, "Goal song"),
    };

    public static readonly IReadOnlyDictionary<string, FieldSpec> Goalie = new Dictionary<string, FieldSpec>(StringComparer.Ordinal)
    {
        ["skill_overall"] = Skater["skill_overall"],
        ["skill_reflex"] = Rating(CategoryGroup, "Reflexes"), ["skill_control"] = Rating(CategoryGroup, "Control"), ["skill_athletics"] = Rating(CategoryGroup, "Athleticism"),
        ["c_gsl"] = Rating(RatingGroup, "Glove low", RatingHint), ["c_gsh"] = Rating(RatingGroup, "Glove high"), ["c_ssl"] = Rating(RatingGroup, "Stick low"), ["c_ssh"] = Rating(RatingGroup, "Stick high"),
        ["c_5hole"] = Rating(RatingGroup, "Five hole"), ["c_positioning"] = Rating(RatingGroup, "Angles"), ["c_breakaway"] = Rating(RatingGroup, "Breakaway"),
        ["c_reboundcontrol"] = Rating(RatingGroup, "Rebound control"), ["c_shotrecover"] = Rating(RatingGroup, "Recover"), ["c_pokecheck"] = Rating(RatingGroup, "Poke check"),
        ["c_vision"] = Rating(RatingGroup, "Vision"), ["c_consistency"] = Rating(RatingGroup, "Consistency"), ["c_puckplayfrequency"] = Rating(RatingGroup, "Puck playing frequency"),
        ["c_speed"] = Rating(RatingGroup, "Speed"), ["c_agility"] = Rating(RatingGroup, "Agility"), ["c_endurance"] = Rating(RatingGroup, "Endurance"), ["c_durability"] = Rating(RatingGroup, "Durability"),
        ["c_passing"] = Rating(RatingGroup, "Passing"), ["c_poise"] = Rating(RatingGroup, "Poise"), ["c_aggression"] = Rating(RatingGroup, "Aggressiveness"),
        ["c_fighting"] = Skater["c_fighting"],
        ["playerstyle"] = new(StyleGroup, "Goalie style", FieldKind.Choice, ChoiceSets.GoalieStyle),
        ["stance"] = new(StyleGroup, "Stance", FieldKind.Choice, ChoiceSets.Stance), ["c_paddledown"] = new(StyleGroup, "Paddle down"),
        ["growthtier"] = Skater["growthtier"], ["growthletter"] = Skater["growthletter"], ["growthspeed"] = Skater["growthspeed"], ["growthage"] = Skater["growthage"], ["growthmod"] = Skater["growthmod"],
        ["c_overall"] = Skater["c_overall"], ["c_potential"] = Skater["c_potential"],
        ["xza"] = new(StyleGroup, "X-Factor (zone)", Hint: "0 = none. Goalie X-Factors are 200 and up."), ["xsa1"] = Skater["xsa1"], ["xsa2"] = Skater["xsa2"], ["xsa3"] = Skater["xsa3"], ["xsa4"] = Skater["xsa4"], ["xsa5"] = Skater["xsa5"],
        ["warmup"] = new(TendencyGroup, "Warm-up"), ["reactionbigsave"] = new(TendencyGroup, "Big-save reaction"), ["reactionpostgame"] = new(TendencyGroup, "Post-game reaction"),
    };

    public static readonly IReadOnlyDictionary<string, FieldSpec> Gear = new Dictionary<string, FieldSpec>(StringComparer.Ordinal)
    {
        ["helmetbrand"] = new(GearGroup, "Helmet", FieldKind.Choice, ChoiceSets.Helmet), ["gloves"] = new(GearGroup, "Gloves", FieldKind.Choice, ChoiceSets.Gloves),
        ["stick"] = new(GearGroup, "Stick", FieldKind.Choice, ChoiceSets.Stick), ["skatebrand"] = new(GearGroup, "Skates", FieldKind.Choice, ChoiceSets.Skates),
        ["visorcolor"] = new(GearGroup, "Visor tint", FieldKind.Choice, ChoiceSets.VisorTint), ["jerseytuck"] = new(GearGroup, "Jersey tuck", FieldKind.Choice, ChoiceSets.JerseyTuck),
        ["pantstypecode"] = new(GearGroup, "Pant length", FieldKind.Choice, ChoiceSets.Pants), ["earguard"] = new(GearGroup, "Ear guards"), ["mouthguard"] = new(GearGroup, "Mouth guard"),
        ["stickcurve"] = new(GearGroup, "Stick curve"), ["stickflex"] = new(GearGroup, "Stick flex"), ["shoulderpads"] = new(GearGroup, "Shoulder pads"),
        ["maskid"] = new(GearGroup, "Mask"), ["masktype"] = new(GearGroup, "Mask type"), ["blocker"] = new(GearGroup, "Blocker"), ["trapper"] = new(GearGroup, "Trapper"),
        ["pads"] = new(GearGroup, "Pads"), ["neckprotector"] = new(GearGroup, "Neck guard"),
        ["showcustomcolors"] = new(GearGroup, "Use his own colours", Hint: "Off: the team's gear colours."), ["showcustomstick"] = new(GearGroup, "Use his own stick colours"),
        ["keepcustomiftraded"] = new(GearGroup, "Keep colours when traded"), ["keepcustomstickiftraded"] = new(GearGroup, "Keep stick colours when traded"),
    };

    public static readonly IReadOnlyDictionary<string, FieldSpec> Roster = new Dictionary<string, FieldSpec>(StringComparer.Ordinal)
    {
        ["team"] = new(LinesGroup, "Team", FieldKind.Team, Hint: "Read only here; move a player on the lines page."),
        ["jersey"] = new(LinesGroup, "Number on this team"),
        ["captain"] = new(LinesGroup, "Captaincy", FieldKind.Choice, ChoiceSets.Captain),
        ["rosterstatus"] = new(LinesGroup, "Dressed", FieldKind.Flag, Hint: "1 on the active roster; 0 scratched or in the minors."),
        ["playerstyle"] = new(LinesGroup, "Archetype on this team", FieldKind.Choice, ChoiceSets.SkaterStyle),
    };

    public static readonly IReadOnlyDictionary<string, FieldSpec> Team = new Dictionary<string, FieldSpec>(StringComparer.Ordinal)
    {
        ["cityname"] = new(TeamNames, "City", FieldKind.Text), ["teamname"] = new(TeamNames, "Team name", FieldKind.Text), ["nickname"] = new(TeamNames, "Nickname", FieldKind.Text),
        ["abbrname"] = new(TeamNames, "Abbreviation", FieldKind.Text), ["artabbr"] = new(TeamNames, "Art abbreviation", FieldKind.Text, Hint: "Names the team's art folders; leave it unless you replace the art."),
        ["mascotcustomname"] = new(TeamNames, "Mascot name", FieldKind.Text),
        ["league"] = new(TeamIdentity, "League"), ["leaguegroup"] = new(TeamIdentity, "League group"), ["divisiongroup"] = new(TeamIdentity, "Division"), ["conferencegroup"] = new(TeamIdentity, "Conference"),
        ["ahlaffiliate"] = new(TeamIdentity, "AHL affiliate", FieldKind.Team), ["rivalteam"] = new(TeamIdentity, "Rival", FieldKind.Team), ["rivalteam2"] = new(TeamIdentity, "Second rival", FieldKind.Team),
        ["arenaid"] = new(TeamIdentity, "Arena"), ["active"] = new(TeamIdentity, "Active"), ["teamtype"] = new(TeamIdentity, "Team type"), ["index"] = new(TeamIdentity, "Index", Hint: "The team's key. Read only."),
        ["overallrating"] = Rating(TeamRatings, "Overall"), ["offenserating"] = Rating(TeamRatings, "Offence"), ["defenserating"] = Rating(TeamRatings, "Defence"), ["goalrating"] = Rating(TeamRatings, "Goaltending"),
    };

    /// <summary>The spec for <paramref name="field"/> of a table of the studio ("bio", "skater", "goalie", "skatergear", "goaliegear", "roster", "team").</summary>
    public static FieldSpec For(string table, T3Db.Field field)
    {
        var dict = table switch { "bio" => Bio, "skater" => Skater, "goalie" => Goalie, "skatergear" or "goaliegear" => Gear, "roster" => Roster, _ => Team };
        return dict.TryGetValue(field.Name, out var spec) ? spec : Guess(table, field);
    }

    public static bool IsReadOnly(string table, string field) => field is "game_id" or "key" or "playerindex" or "index" || table == "roster" && field == "team";

    static readonly Regex Digits = new("\\d+", RegexOptions.Compiled);

    /// <summary>Group, label and kind for a field the catalogue does not name.</summary>
    public static FieldSpec Guess(string table, T3Db.Field f)
    {
        var kind = f.Type == 0 ? FieldKind.Text : f.Depth == 1 ? FieldKind.Flag : f.Depth == 24 || f.Name.Contains("color", StringComparison.Ordinal) && f.Depth >= 20 ? FieldKind.Colour
            : f.Name.StartsWith("c_", StringComparison.Ordinal) || f.Name.StartsWith("skill_", StringComparison.Ordinal) ? FieldKind.Rating : FieldKind.Number;
        string name = f.Name;
        if (table == "team")
        {
            var group = name.StartsWith("goaliemask", StringComparison.Ordinal) ? TeamMasks : name.StartsWith("franchise", StringComparison.Ordinal) ? TeamFranchise
                : kind == FieldKind.Colour ? TeamColours : TeamPresentation;
            return new(group, Pretty(name), kind);
        }
        if (name.StartsWith("trait", StringComparison.Ordinal) && int.TryParse(name[5..], out var n))
            return new(TraitGroup, $"Trait {n + 1}", kind == FieldKind.Flag ? FieldKind.Flag : FieldKind.Choice, table == "goalie" ? null : ChoiceSets.Trait);
        if (name.StartsWith("skatingstyle", StringComparison.Ordinal)) return new(TendencyGroup, Pretty(name), kind);
        if (table is "skatergear" or "goaliegear")
            return new(kind == FieldKind.Colour ? GearColourGroup : GearGroup, Pretty(name), kind);
        if (table == "bio")
        {
            if (name.StartsWith("personality", StringComparison.Ordinal) || name.StartsWith("morale", StringComparison.Ordinal) || name.StartsWith("friendenemy", StringComparison.Ordinal)
                || name is "swagger" or "playercomp" or "playerclassid" or "expansiondraftstatus" or "franchisedraftyear" or "franchisestartage")
                return new(PersonalityGroup, Pretty(name), kind);
            if (name.StartsWith("nhl", StringComparison.Ordinal) || name == "progamesplayedcareer") return new(DraftGroup, Pretty(name), kind);
            if (name.StartsWith("mascot", StringComparison.Ordinal) || name == "ismascot") return new(OtherGroup, Pretty(name), kind);
        }
        return new(OtherGroup, Pretty(name), kind);
    }

    /// <summary>"stanchionzone1" as "Stanchionzone 1", "goaliemask0_starter" as "Goaliemask 0 starter".</summary>
    public static string Pretty(string name)
    {
        var spaced = Digits.Replace(name.Replace('_', ' '), m => " " + m.Value + " ").Trim();
        spaced = Regex.Replace(spaced, "\\s+", " ");
        return spaced.Length == 0 ? name : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }
}

/// <summary>Turns a stored value into what the studio shows and back, per <see cref="FieldKind"/>.</summary>
public static class FieldFormat
{
    public static string Show(FieldKind kind, long v, IReadOnlyList<ChoiceSets.Choice>? choices = null) => kind switch
    {
        FieldKind.Year => v < 0 ? "none" : (v + 1900).ToString(CultureInfo.InvariantCulture),
        FieldKind.Month => (v + 1).ToString(CultureInfo.InvariantCulture),
        FieldKind.Day => (v + 1).ToString(CultureInfo.InvariantCulture),
        FieldKind.Height => $"{(v + 54) / 12}'{(v + 54) % 12}\"",
        FieldKind.Weight => $"{v + 120} lb",
        FieldKind.Money => Money(v * 5000),
        FieldKind.Choice or FieldKind.Team => choices?.FirstOrDefault(c => c.Value == v)?.Name ?? v.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"12.5M", "850K" or "850000" from a number of dollars.</summary>
    public static string Money(long dollars) => dollars >= 1_000_000 && dollars % 10_000 == 0 ? $"{dollars / 1_000_000.0:0.##}M" : dollars >= 1_000 && dollars % 1_000 == 0 ? $"{dollars / 1_000}K" : dollars.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads what the user typed back into the stored value. Returns false with a message when it cannot be read.</summary>
    public static bool TryParse(FieldKind kind, string text, out long value, out string error)
    {
        value = 0; error = "";
        text = text.Trim();
        switch (kind)
        {
            case FieldKind.Year:
                if (text.Equals("none", StringComparison.OrdinalIgnoreCase) || text == "") { value = -1; return true; }
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y is >= 1900 and <= 2155) { value = y - 1900; return true; }
                error = "Enter a four-digit year, for example 2026."; return false;
            case FieldKind.Month:
            case FieldKind.Day:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d >= 1) { value = d - 1; return true; }
                error = "Enter a number from 1."; return false;
            case FieldKind.Height:
            {
                var m = Regex.Match(text, "^(\\d+)\\s*['’]\\s*(\\d{1,2})?\\s*\"?$");
                long inches;
                if (m.Success) inches = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 12 + (m.Groups[2].Success ? long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0);
                else if (!long.TryParse(text.TrimEnd('"', ' '), NumberStyles.Integer, CultureInfo.InvariantCulture, out inches)) { error = "Enter a height like 6'2\" or 74 (inches)."; return false; }
                value = inches - 54; return true;
            }
            case FieldKind.Weight:
                if (long.TryParse(text.Replace("lb", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lb)) { value = lb - 120; return true; }
                error = "Enter a weight in pounds."; return false;
            case FieldKind.Money:
            {
                var t = text.Replace("$", "").Replace(",", "").Trim();
                double mult = 1;
                if (t.EndsWith('M') || t.EndsWith('m')) { mult = 1_000_000; t = t[..^1]; }
                else if (t.EndsWith('K') || t.EndsWith('k')) { mult = 1_000; t = t[..^1]; }
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) { value = (long)Math.Round(amount * mult / 5000); return true; }
                error = "Enter dollars, for example 12.5M, 850K or 850000."; return false;
            }
            default:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
                error = "Enter a whole number."; return false;
        }
    }
}
