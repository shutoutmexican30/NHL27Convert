using Suite.Convert27;
using Suite.Core;
using Xunit;

namespace Suite.Tests;

public class ModLibraryRepairTests
{
    const string Folder = "content/character/skater/league/nhl/arizona/adidas/away";
    const string BaseSlot = Folder + "/jersey_adidas_arizona_away_base_color";
    const string NormalSlot = Folder + "/jersey_adidas_arizona_away_base_normal";
    const string Normal27 = @"C:\cache\utah_thirteen_jersey_base_normal.png";
    const string Base27 = @"C:\cache\utah_thirteen_jersey_base_color.png";
    const string Composite = @"C:\images\composite\away.png";

    [Fact]
    public void Utah_legacy_base_is_repaired_but_pants_and_socks_stay_composited()
    {
        var mod = new ModFile
        {
            Name = "Utah",
            Replacements =
            [
                new(BaseSlot, Composite, Alpha: "auto"),
                new(NormalSlot, Normal27),
                new(Folder + "/pant_adidas_arizona_away_base_color", Composite, Alpha: "auto"),
                new(Folder + "/sock_adidas_arizona_away_base_color", Composite, Alpha: "auto"),
            ],
        };

        Assert.Equal(1, ModApply.RepairLegacyJerseyBases([mod], _ => true));
        Assert.Equal(Base27, mod.Replacements[0].Source);
        Assert.Equal("auto", mod.Replacements[0].Alpha);
        Assert.Equal(Composite, mod.Replacements[2].Source);
        Assert.Equal(Composite, mod.Replacements[3].Source);
        Assert.Empty(ModApply.LegacyBaseProblems([mod]));
    }

    [Fact]
    public void Missing_exact_base_blocks_apply_instead_of_duplicating_logos()
    {
        var mod = new ModFile { Name = "Old export", Replacements = [new(BaseSlot, Composite), new(NormalSlot, Normal27)] };
        Assert.Equal(0, ModApply.RepairLegacyJerseyBases([mod], _ => false));
        var issue = Assert.Single(ModApply.LegacyBaseProblems([mod]));
        Assert.Contains("Old export", issue);
        Assert.Contains("base_color.png", issue);
    }

    [Fact]
    public void Effective_mod_list_counts_each_slot_once_and_later_mod_wins()
    {
        var older = new ModFile { Name = "older", Replacements = [new(BaseSlot, "old.png"), new(NormalSlot, Normal27)] };
        var newer = new ModFile { Name = "newer", Replacements = [new(BaseSlot, Base27)] };

        Assert.Equal(3, older.Replacements.Count + newer.Replacements.Count);
        Assert.Equal(2, ModApply.TotalReplacements([older, newer]));
        Assert.Equal(1, ModApply.OverlappingEntries([older, newer]));
        Assert.Equal(Base27, ModApply.EffectiveReplacements([older, newer]).Single(r => r.Slot == BaseSlot).Source);
    }
}
