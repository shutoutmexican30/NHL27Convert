using System.Buffers.Binary;
using Suite.Rosters;

namespace Suite.Rosters.Tests;

public sealed class EquipmentEditorTests
{
    static string Fixture(params string[] parts) => Path.GetFullPath(Path.Combine(
        [AppContext.BaseDirectory, "..", "..", "..", "..", "..", "reference", "nhl24-full-csv", "gamemodes", "database", "t3db", .. parts]));
    static string Meta => Fixture("nhl_game-meta.xml");
    static Dictionary<string, byte[]> Sources() => new(StringComparer.Ordinal)
    {
        ["nhl_game"] = File.ReadAllBytes(Fixture("nhl_game.db")),
        ["initial_experience"] = File.ReadAllBytes(Fixture("..", "..", "roster", "initial_experience.db")),
    };
    static Dictionary<string, byte[]> Prepared() => JerseyRowPlanner.Build(Sources(), Meta,
        [new NewJerseyRowRequest(0, 25, false), new NewJerseyRowRequest(0, 24, true)]).Databases.ToDictionary(kv => kv.Key, kv => kv.Value);
    static EquipmentEditorDocument Open() => EquipmentEditorDocument.Load(Prepared(), Meta);
    static EquipmentEditKey Donor(EquipmentEditorDocument doc) => doc.Rows(team: 0, equipment: 4, variant: 0).First().Key;

    [Fact]
    public void Explicit_duplication_writes_all_15_zones_and_preserves_stock_and_other_tables()
    {
        var doc = Open();
        var home = doc.Duplicate(Donor(doc), 25);
        var away = doc.Duplicate(Donor(doc), 24);
        var db = T3Db.Load(doc.OriginalDatabases["nhl_game"], Meta);
        var jerseys = db.Tables[JerseyRowPlanner.TableName];
        long targetArt = Enumerable.Range(0, jerseys.InUse).Where(r => db.GetInt(jerseys, r, "teamid") == 0
            && db.GetInt(jerseys, r, "variant") == 25).Select(r => db.GetInt(jerseys, r, "teamartid")).Distinct().Single();
        Assert.Equal(targetArt, home.JerseyArtId);
        Assert.Equal(targetArt, away.JerseyArtId);
        var colors = NewUniformEquipmentPlan.Zones.Select((z, i) => (z, value: (long)(0x102030 + i * 0x10203)))
            .ToDictionary(x => x.z, x => x.value, StringComparer.Ordinal);
        doc.SetZones(home, colors);
        var plan = doc.BuildPlan();
        Assert.Equal(2, plan.Inserted.Count);
        Assert.Equal(2, plan.ChangedRows);
        Assert.Equal(4, plan.Rows.Count);
        plan.Verify(plan.Databases);
        foreach (var name in plan.Databases.Keys)
        {
            var before = T3Db.Load(plan.OriginalDatabases[name], Meta);
            var after = T3Db.Load(plan.Databases[name], Meta);
            Assert.Equal(before.Tables.Count, after.Tables.Count);
            foreach (var table in before.Tables.Values.Where(t => t.Name != NewUniformEquipmentPlan.TableName))
                Assert.Equal(plan.OriginalDatabases[name].AsSpan(table.Start, table.End - table.Start).ToArray(),
                    plan.Databases[name].AsSpan(table.Start, table.End - table.Start).ToArray());
            var equipment = after.Tables[NewUniformEquipmentPlan.TableName];
            var row = EquipmentEditorDocument.Find(after, home).Single().Row;
            foreach (var zone in NewUniformEquipmentPlan.Zones) Assert.Equal(colors[zone], after.GetInt(equipment, row, zone));
        }
    }

    [Fact]
    public void Duplicate_runtime_keys_and_wrong_art_bindings_are_refused()
    {
        var setup = Open();
        var reserved = setup.Duplicate(Donor(setup), 25);
        var sources = setup.BuildPlan().Databases.ToDictionary(kv => kv.Key, kv => kv.Value);
        var db = T3Db.Load(sources["nhl_game"], Meta);
        var table = db.Tables[NewUniformEquipmentPlan.TableName];
        int donor = EquipmentEditorDocument.Find(db, reserved).Single().Row;
        int other = Enumerable.Range(0, table.InUse).First(r => db.GetInt(table, r, "team") != 0 && db.GetInt(table, r, "equipment") == 4);
        foreach (string field in new[] { "team", "brand", "jerseyartid", "equipment", "era", "variant" })
            db.SetInt(table, other, field, db.GetInt(table, donor, field));
        sources["nhl_game"] = db.Save();
        Assert.Throws<InvalidDataException>(() => EquipmentEditorDocument.Load(sources, Meta));

        var doc = Open();
        var key = doc.Duplicate(Donor(doc), 25);
        Assert.Throws<InvalidDataException>(() => doc.SetZones(key with { JerseyArtId = key.JerseyArtId + 1 },
            NewUniformEquipmentPlan.Zones.ToDictionary(z => z, _ => 0L)));
    }

    [Fact]
    public void Duplicate_requests_missing_capacity_and_stock_recolors_are_refused()
    {
        var doc = Open();
        var donor = Donor(doc);
        doc.Duplicate(donor, 25);
        Assert.Throws<InvalidDataException>(() => doc.Duplicate(donor, 25));
        Assert.Throws<InvalidDataException>(() => doc.SetZones(donor, NewUniformEquipmentPlan.Zones.ToDictionary(z => z, _ => 0L)));

        var sources = Prepared();
        foreach (var name in sources.Keys.ToList())
        {
            var bytes = sources[name];
            var db = T3Db.Load(bytes, Meta);
            var table = db.Tables[NewUniformEquipmentPlan.TableName];
            uint header = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(table.Start + 16));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(table.Start + 16), (uint)table.Slots << 16 | header & 0xFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(table.Start + 32), T3Db.Crc(bytes.AsSpan(table.Start, 32)));
        }
        var full = EquipmentEditorDocument.Load(sources, Meta);
        Assert.Throws<InvalidDataException>(() => full.Duplicate(Donor(full), 25));
    }

    [Fact]
    public void Reopening_a_saved_plan_is_idempotent()
    {
        var doc = Open();
        Assert.False(doc.BuildPlan().HasChanges);
        var plan = doc.Duplicate(Donor(doc), 25);
        var saved = doc.BuildPlan();
        var again = EquipmentEditorDocument.Load(saved.Databases, Meta).BuildPlan();
        Assert.False(again.HasChanges);
        Assert.Empty(again.Rows);
        Assert.Equal(saved.Databases, again.Databases);
    }
}
