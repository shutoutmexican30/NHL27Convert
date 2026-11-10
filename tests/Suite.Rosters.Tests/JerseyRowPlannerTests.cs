using System.Buffers.Binary;
using Suite.Rosters;
using Xunit;

namespace Suite.Rosters.Tests;

public sealed class JerseyRowPlannerTests
{
    static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "../../../../../Debugging/row-planner", name));
    static string Meta => Fixture("nhl_game-meta.xml");

    static Dictionary<string, byte[]> Sources() => new(StringComparer.Ordinal)
    {
        ["nhl_game"] = File.ReadAllBytes(Fixture("nhl_game.db")),
        ["initial_experience"] = File.ReadAllBytes(Fixture("initial_experience.db")),
    };

    [Fact]
    public void Sixty_four_new_uniforms_are_coordinated_idempotent_and_preserve_original_rows()
    {
        var sources = Sources();
        var requests = Requests(sources["nhl_game"]);
        Assert.Equal(64, requests.Count);
        var plan = JerseyRowPlanner.Build(sources, Meta, requests);
        Assert.Equal(128, plan.InsertedRows); // 64 in each coordinated database.
        Assert.Equal(2, plan.ChangedDatabases.Count);

        foreach (var (name, before) in sources)
        {
            var oldDb = T3Db.Load(before, Meta);
            var newDb = T3Db.Load(plan.Databases[name], Meta);
            Assert.Equal(oldDb.Tables[JerseyRowPlanner.TableName].InUse + 64,
                newDb.Tables[JerseyRowPlanner.TableName].InUse);
            Assert.Equal(before.Length, plan.Databases[name].Length);
            Assert.Equal(64, plan.Rows.Count(r => r.Database == name && !r.AlreadyExists));
            Assert.Equal(32, plan.Rows.Where(r => r.Database == name && !r.AlreadyExists && !r.IsLight).Select(r => r.Team).Distinct().Count());
            Assert.Equal(32, plan.Rows.Where(r => r.Database == name && !r.AlreadyExists && r.IsLight).Select(r => r.Team).Distinct().Count());
        }

        var repeat = JerseyRowPlanner.Build(plan.Databases, Meta, requests);
        Assert.Equal(0, repeat.InsertedRows);
        Assert.Empty(repeat.ChangedDatabases);
        foreach (var name in sources.Keys) Assert.Equal(plan.Databases[name], repeat.Databases[name]);
    }

    [Fact]
    public void Duplicate_requests_are_rejected_before_any_output_is_returned()
    {
        var ex = Assert.Throws<InvalidDataException>(() => JerseyRowPlanner.Build(Sources(), Meta,
            [new NewJerseyRowRequest(0, 25, false), new NewJerseyRowRequest(0, 25, true)]));
        Assert.Contains("Duplicate requested", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Insufficient_spare_capacity_is_a_hard_failure()
    {
        var source = File.ReadAllBytes(Fixture("nhl_game.db"));
        var db = T3Db.Load((byte[])source.Clone(), Meta);
        var table = db.Tables[JerseyRowPlanner.TableName];
        int added = 0;
        while (table.InUse < table.Slots)
        {
            table = db.InsertRecord(table, table.InUse, 0);
            db.SetInt(table, table.InUse - 1, "teamid", 32 + added / 32);
            db.SetInt(table, table.InUse - 1, "variant", added % 32);
            added++;
        }
        byte[] full = db.Save();
        var ex = Assert.Throws<InvalidDataException>(() => JerseyRowPlanner.Build(
            new Dictionary<string, byte[]> { ["nhl_game"] = full }, Meta,
            [new NewJerseyRowRequest(0, 25, false)]));
        Assert.Contains("spare slots", ex.Message, StringComparison.Ordinal);
    }

    static List<NewJerseyRowRequest> Requests(byte[] source)
    {
        var db = T3Db.Load(source, Meta);
        var table = db.Tables[JerseyRowPlanner.TableName];
        var requests = new List<NewJerseyRowRequest>();
        for (int team = 0; team < 32; team++)
        {
            var used = Enumerable.Range(0, table.InUse)
                .Where(r => db.GetInt(table, r, "teamid") == team && db.GetInt(table, r, "style") == 2)
                .Select(r => (int)db.GetInt(table, r, "variant")).ToHashSet();
            var available = Enumerable.Range(2, 24).Reverse().Where(v => !used.Contains(v)).Take(2).ToList();
            Assert.Equal(2, available.Count);
            requests.Add(new NewJerseyRowRequest(team, available[0], false));
            requests.Add(new NewJerseyRowRequest(team, available[1], true));
        }
        return requests;
    }
}
