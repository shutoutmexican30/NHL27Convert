using System.IO;
using Suite.Core;
using Suite.Core.Frostbite;
using Suite.Rosters;

namespace Suite.App.Pages;

/// <summary>Where a roster database comes from and goes back to: a saved roster, a loose .db file, or one of the game's own databases.
/// Shared by the Teams & database page and the Database studio.</summary>
internal interface ISource
{
    string Label { get; }
    byte[] ReadDb(out byte[] trailer);
    string WriteDb(byte[] dbWithTrailer);
}

internal sealed class SaveSource(SavedRoster save) : ISource
{
    public string Label => $"Saved roster: {Path.GetFileName(Path.GetDirectoryName(save.Dir)!)}/{Path.GetFileName(save.Dir)}";
    public byte[] ReadDb(out byte[] trailer)
    {
        var blob = save.ReadDb();
        var n = SavedRoster.DbLength(blob);
        trailer = blob[n..];
        return blob[..n];
    }
    public string WriteDb(byte[] dbWithTrailer)
    {
        var backup = SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "save-backups"));
        save.WriteDb(dbWithTrailer, backup);
        return $"Wrote {save.Dir}\nBackup: {Path.Combine(backup, save.Name)}";
    }
}

internal sealed class FileSource(string path) : ISource
{
    public string Path => path;
    public string Label => $"File: {path}";
    public byte[] ReadDb(out byte[] trailer)
    {
        var blob = File.ReadAllBytes(path);
        var n = SavedRoster.DbLength(blob);
        trailer = blob.Length - n is > 0 and <= 64 ? blob[n..] : [];
        return blob[..n];
    }
    public string WriteDb(byte[] dbWithTrailer)
    {
        var backup = path + ".before-edit";
        if (!File.Exists(backup)) File.Copy(path, backup);
        var tmp = path + ".cims-tmp";
        File.WriteAllBytes(tmp, dbWithTrailer);
        File.Move(tmp, path, true);
        return $"Wrote {path}\nBackup: {backup}";
    }
}

/// <summary>One of the game's own databases (nhl_game holds stockteamjerseys / stockteamequipmenttable, the tables the
/// game builds its uniform and equipment lists from), read from and written back into the apply target's game files.</summary>
internal sealed class GameDbSource(SuiteSettings settings, string game, string database) : ISource
{
    byte[]? _original;
    public string Label => $"Game database: {database} ({game})";
    public byte[] ReadDb(out byte[] trailer)
    {
        trailer = [];
        _original = Suite.Convert27.GameDatabaseEdit.Read(game, settings, database);
        return (byte[])_original.Clone();
    }
    public string WriteDb(byte[] dbWithTrailer)
    {
        if (_original is null) throw new InvalidOperationException("Open the database first.");
        var log = new List<string>();
        var backup = Suite.Convert27.GameDatabaseEdit.Write(game, settings, database, _original, dbWithTrailer, log.Add);
        _original = (byte[])dbWithTrailer.Clone();
        return log.LastOrDefault() ?? $"Wrote {database}. Backup: {backup}";
    }
}


internal static class DbSources
{
    public static string FindMeta(ISource source, SuiteSettings settings)
    {
        // A file source usually sits next to its meta (a stock export keeps both in the same folder).
        if (source is FileSource f)
        {
            var beside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!, "nhl_game-meta.xml");
            if (File.Exists(beside)) return beside;
        }
        var stock = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game-meta.xml");
        if (File.Exists(stock)) return stock;
        // Last resort: pull the meta straight out of the game files and cache it under the suite's local folder.
        if (!string.IsNullOrEmpty(settings.ApplyTargetDir))
        {
            try
            {
                var bytes = new LegacyFiles(settings.ApplyTargetDir, settings).Read("gamemodes/database/t3db/nhl_game-meta.xml");
                var cache = Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "meta-cache")), "nhl_game-meta.xml");
                File.WriteAllBytes(cache, bytes);
                return cache;
            }
            catch { /* reported below */ }
        }
        throw new FileNotFoundException("nhl_game-meta.xml was not found next to the database, in the work folder, or in the game. Run a roster sync once (Teams & roster page) and try again.");
    }

    /// <summary>Every database the studio can open: the game's five, saved rosters next to the apply target, and the stock export.</summary>
    public static List<ISource> List(SuiteSettings settings)
    {
        var list = new List<ISource>();
        if (!string.IsNullOrEmpty(settings.ApplyTargetDir) && Directory.Exists(settings.ApplyTargetDir))
        {
            foreach (var name in Suite.Convert27.GameDatabaseEdit.Databases) list.Add(new GameDbSource(settings, settings.ApplyTargetDir, name));
            foreach (var save in SavedRoster.SavedataRoots(settings.ApplyTargetDir).SelectMany(SavedRoster.Find)) list.Add(new SaveSource(save));
        }
        var stock = Path.Combine(SuitePaths.Local, "dbwork", "stock", "gamemodes", "database", "t3db", "nhl_game.db");
        if (File.Exists(stock)) list.Add(new FileSource(stock));
        return list;
    }
}