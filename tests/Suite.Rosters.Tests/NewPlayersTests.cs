using System.Text;
using System.Xml.Linq;
using Suite.Rosters;

namespace Suite.Rosters.Tests;

public sealed class NewPlayersTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "cims-new-player-tests", Guid.NewGuid().ToString("N"));
    string Meta => Path.Combine(dir, "meta.xml");
    const string Bio = "exhibitionplayerbiotable";

    public NewPlayersTests()
    {
        Directory.CreateDirectory(dir);
        string[] columns = ["game_id", "firstname", "lastname", "year", "month", "day", "position", "positionprimary",
            "handedness", "jersey", "nickname", "team", "proteam", "artid", "hasportrait", "audioid", "surnameid", "headid"];
        Table(Bio, columns, ["100", "Donor", "Player", "100", "0", "0", "0", "0", "0", "10", "", "0", "0", "5", "1", "5", "5", "60001"]);
        Table("exhibitionplayers", ["index", "playerid", "table"], ["1", "100", "0"]);
        Table("exhibitionskaterai", ["game_id", "skill_overall"], ["100", "80"]);
        Table("exhibitionskaterequipment", ["game_id", "stick"], ["100", "42"]);
        new XDocument(new XElement("database", new XElement("table", new XAttribute("name", Bio),
            columns.Select(c => new XElement("field", new XAttribute("name", c),
                new XAttribute("type", c is "firstname" or "lastname" or "nickname" ? "FIELDTYPE_STRING" : "FIELDTYPE_INT"),
                new XAttribute("depth", "128"), new XAttribute("rangelow", "-1"), new XAttribute("rangehigh", "65535")))))).Save(Meta);
    }

    void Table(string name, string[] columns, string[] row)
    {
        var filler = string.Join(",", columns.Select(_ => "0"));
        File.WriteAllText(T3Csv.PathOf(dir, name), string.Join(",", columns) + "\r\n" + string.Join(",", row)
            + "\r\n" + filler + "\r\n" + filler + "\r\n", new UTF8Encoding(true));
    }

    static NewPlayer Player(int id, string first) => new(first + " Rookie", "MIN",
        new ApiPlayer(id, first, "Rookie", 97, "C", new DateOnly(2004, 1, 2), null, "MIN", "L"),
        id, id, "100", "Donor Player", null, false);

    [Fact]
    public void Rejected_bio_creates_no_linked_rows_and_leaves_files_unchanged()
    {
        var before = Directory.GetFiles(dir).ToDictionary(p => p, File.ReadAllBytes);
        var notes = new List<string>();
        Assert.Empty(NewPlayers.AddTo(dir, dir, Meta, [Player(200, new string('x', 20))], null, notes));
        Assert.Single(notes);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Mixed_batch_adds_only_valid_players_and_is_idempotent()
    {
        var notes = new List<string>();
        NewPlayer[] plan = [Player(200, new string('x', 20)), Player(201, "Valid")];
        Assert.Equal(4, NewPlayers.AddTo(dir, dir, Meta, plan, null, notes).Count);
        foreach (var table in new[] { Bio, "exhibitionplayers", "exhibitionskaterai", "exhibitionskaterequipment" })
        {
            var t = T3Csv.Load(dir, table);
            Assert.Equal(3, t.Rows.Count);
            Assert.Equal(2, t.ValidCount);
            var key = table == "exhibitionplayers" ? "playerid" : "game_id";
            Assert.Equal(new[] { 100, 201 }, t.Rows.Take(t.ValidCount).Select(r => t.Int(r, key)));
        }
        var bio = T3Csv.Load(dir, Bio);
        Assert.Equal("60001", bio.Get(bio.Rows[1], "headid"));
        Assert.Equal("0", bio.Get(bio.Rows[1], "hasportrait"));
        var before = Directory.GetFiles(dir).ToDictionary(p => p, File.ReadAllBytes);
        Assert.Empty(NewPlayers.AddTo(dir, dir, Meta, plan, null, notes));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Existing_snapshot_player_gets_main_appearance_without_changing_his_index()
    {
        NewPlayer[] plan = [Player(201, "Valid")];
        NewPlayers.AddTo(dir, dir, Meta, plan, null, []);
        // Alignment examines both goalie tables too, even for an all-skater batch.
        Table("exhibitiongoalieai", ["game_id", "skill_overall"], ["999", "80"]);
        Table("exhibitiongoalieequipment", ["game_id", "stick"], ["999", "42"]);
        var older = Path.Combine(dir, "older");
        Directory.CreateDirectory(older);
        foreach (var file in Directory.GetFiles(dir, "*.csv")) File.Copy(file, Path.Combine(older, Path.GetFileName(file)));
        var bio = T3Csv.Load(older, Bio);
        bio.Set(bio.Rows[1], "headid", 60999); bio.Save(older);
        var ai = T3Csv.Load(older, "exhibitionskaterai");
        ai.Set(ai.Rows[1], "skill_overall", 1); ai.Save(older);
        var ep = T3Csv.Load(older, "exhibitionplayers");
        ep.Set(ep.Rows[1], "index", 999); ep.Save(older);
        var changed = NewPlayers.AlignPlannedWithMain(older, dir, plan);
        Assert.Contains(Bio, changed);
        Assert.Contains("exhibitionskaterai", changed);
        Assert.Equal("60001", T3Csv.Load(older, Bio).Get(T3Csv.Load(older, Bio).Rows[1], "headid"));
        Assert.Equal("80", T3Csv.Load(older, "exhibitionskaterai").Get(T3Csv.Load(older, "exhibitionskaterai").Rows[1], "skill_overall"));
        Assert.Equal("999", T3Csv.Load(older, "exhibitionplayers").Get(T3Csv.Load(older, "exhibitionplayers").Rows[1], "index"));
        Assert.Empty(NewPlayers.AlignPlannedWithMain(older, dir, plan));
    }

    public void Dispose() => Directory.Delete(dir, true);
}
