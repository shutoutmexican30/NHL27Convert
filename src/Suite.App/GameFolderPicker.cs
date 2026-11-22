using System.IO;
using System.Windows;
using Microsoft.Win32;
using Suite.Core;

namespace Suite.App;

/// <summary>The standard Windows folder dialog for choosing an NHL24PC folder, and saving the choice to settings.json.</summary>
public static class GameFolderPicker
{
    /// <summary>Shows the folder dialog starting at <paramref name="initial"/> (or its nearest existing parent). Null = cancelled.</summary>
    public static string? PickFolder(string title, string? initial)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (ExistingParent(initial) is { } start) dlg.InitialDirectory = start;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    /// <summary>Picks an NHL 24 folder; warns (and lets the user pick again or keep it) when it doesn't look like one.</summary>
    public static string? PickNhl24Folder(string title, string? initial)
    {
        while (true)
        {
            var folder = PickFolder(title, initial);
            if (folder is null) return null;
            if (SuiteSettings.IsNhl24Dir(folder)) return folder;
            var answer = MessageBox.Show(
                $"{folder}\n\ndoesn't look like an NHL 24 folder (no game\\package.mft, or package.mft + Data).\n\nUse it anyway?",
                title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) return folder;
            if (answer == MessageBoxResult.Cancel) return null;
            initial = folder;
        }
    }

    /// <summary>
    /// Points the active build at a new game folder (adds a build when none exists) and saves settings.json.
    /// The apply target follows the game folder when it was the same folder (or unset), so a moved game keeps working.
    /// </summary>
    public static void SetActiveGameFolder(SuiteSettings settings, string folder)
    {
        folder = folder.Trim();
        var build = settings.ActiveGameBuild;
        if (build is null)
        {
            settings.UseGameBuild(folder, folder);
        }
        else
        {
            bool applyFollows = string.IsNullOrWhiteSpace(build.ApplyTargetDir) || SamePath(build.ApplyTargetDir, build.GameDir);
            build.GameDir = folder;
            if (applyFollows) build.ApplyTargetDir = folder;
            settings.SelectGameBuild(build.Id);
        }
        settings.Save();
    }

    static string? ExistingParent(string? path)
    {
        try
        {
            for (var p = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim()); p is not null; p = Path.GetDirectoryName(p))
                if (Directory.Exists(p)) return p;
        }
        catch { }
        return null;
    }

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a.Trim()).TrimEnd('\\'), Path.GetFullPath(b.Trim()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase); }
    }
}
