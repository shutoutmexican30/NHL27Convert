using System.Text.Json;
using Suite.Core;
using Xunit;

namespace Suite.Tests;

public class GameBuildProfileTests
{
    [Fact]
    public void Legacy_settings_migrate_once_and_preserve_a_blank_apply_target()
    {
        var settings = JsonSerializer.Deserialize<SuiteSettings>("""
            {
              "Nhl24GameDir": "C:\\Games\\NHL24PC",
              "ApplyTargetDir": ""
            }
            """)!;

        Assert.True(settings.EnsureGameBuilds());
        var build = Assert.Single(settings.GameBuilds);
        Assert.Equal(build.Id, settings.ActiveGameBuildId);
        Assert.Equal("C:\\Games\\NHL24PC", settings.Nhl24GameDir);
        Assert.Equal("", settings.ApplyTargetDir);
        Assert.False(settings.EnsureGameBuilds());
    }

    [Fact]
    public void Selecting_a_build_switches_source_and_apply_target_together()
    {
        var settings = new SuiteSettings();
        settings.UseGameBuild(@"C:\Games\V32", @"C:\Games\V32", "NHL24PC V3.2");
        settings.UseGameBuild(@"C:\Games\v092b", @"C:\Games\v092b", "NHL24 PC v0.9.2b");

        Assert.Equal("NHL24 PC v0.9.2b", settings.ActiveGameBuild?.Name);
        Assert.True(settings.SelectGameBuild(settings.GameBuilds[0].Id));
        Assert.Equal(@"C:\Games\V32", settings.Nhl24GameDir);
        Assert.Equal(@"C:\Games\V32", settings.ApplyTargetDir);
        Assert.False(settings.SelectGameBuild("not-a-build"));
        Assert.Equal(@"C:\Games\V32", settings.Nhl24GameDir);
    }

    [Fact]
    public void Configured_profiles_restore_the_legacy_active_source_on_load()
    {
        var settings = JsonSerializer.Deserialize<SuiteSettings>("""
            {
              "Nhl24GameDir": "C:\\Games\\v092b",
              "ApplyTargetDir": "",
              "GameBuilds": [
                { "Id": "v32", "Name": "V3.2", "GameDir": "C:\\Games\\V32", "ApplyTargetDir": "C:\\Games\\V32" },
                { "Id": "v092b", "Name": "v0.9.2b", "GameDir": "C:\\Games\\v092b", "ApplyTargetDir": "C:\\Games\\v092b" }
              ]
            }
            """)!;

        Assert.True(settings.EnsureGameBuilds());
        Assert.Equal("v092b", settings.ActiveGameBuildId);
        Assert.Equal(@"C:\Games\v092b", settings.ApplyTargetDir);
        Assert.Equal(2, settings.GameBuilds.Count);
    }

    [Fact]
    public void Changing_a_folder_picker_reuses_the_build_and_replaces_its_apply_target()
    {
        var settings = new SuiteSettings();
        settings.UseGameBuild(@"C:\Games\First", @"C:\Games\First", "First");
        var firstId = settings.ActiveGameBuildId;
        settings.UseGameBuild(@"C:\Games\Second", @"C:\Games\Second", "Second");

        Assert.Equal(2, settings.GameBuilds.Count);
        Assert.NotEqual(firstId, settings.ActiveGameBuildId);
        Assert.Equal(settings.Nhl24GameDir, settings.ApplyTargetDir);
        Assert.Equal(@"C:\Games\Second", settings.ApplyTargetDir);
    }
}
