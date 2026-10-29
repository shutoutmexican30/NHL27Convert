using Suite.Core;

namespace Suite.Convert27;

/// <summary>An NHL 27 texture as the decoder needs it (a view of an <see cref="AssetEntry"/> with a PS5 location).</summary>
public sealed class Nhl27Texture(AssetEntry e)
{
    public AssetEntry Entry { get; } = e;
    public string Name => Entry.Path;
    public int Width => Entry.Width;
    public int Height => Entry.Height;
    public string Format => Entry.Format ?? "";
    public int Pkg => Entry.Location?.Package ?? 0;
    public int Cas => Entry.Location?.Cas ?? 0;
    public string Tree => Entry.Location?.Tree ?? "Data";
    public long Offset => Entry.Location?.Offset ?? 0;
    public long Size => Entry.Location?.Size ?? 0;
}

/// <summary>An NHL 24 texture slot (a view of an <see cref="AssetEntry"/>).</summary>
public sealed class Nhl24Slot(AssetEntry e)
{
    public AssetEntry Entry { get; } = e;
    public string Name => Entry.Path;
    public bool Array => Entry.Kind == AssetKind.TextureArray;
}

/// <summary>The matcher's view of the NHL 27 <see cref="AssetIndex"/>.</summary>
public sealed class Nhl27Catalog
{
    public AssetIndex Index { get; }
    public IReadOnlyDictionary<string, Nhl27Texture> ByName { get; }
    public IReadOnlyDictionary<string, List<Nhl27Texture>> ByFolder { get; }

    public Nhl27Catalog(AssetIndex index)
    {
        Index = index;
        var all = index.Entries.Where(e => e.Kind is AssetKind.Texture or AssetKind.TextureArray).Select(e => new Nhl27Texture(e)).ToList();
        ByName = all.ToDictionary(t => t.Name, StringComparer.Ordinal);
        ByFolder = all.GroupBy(t => FolderOf(t.Name)).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    public static string FolderOf(string name) => name[..Math.Max(0, name.LastIndexOf('/'))];
}

/// <summary>The matcher's view of the NHL 24 <see cref="AssetIndex"/>.</summary>
public sealed class Nhl24Catalog
{
    public AssetIndex Index { get; }
    public IReadOnlyDictionary<string, Nhl24Slot> ByName { get; }
    public IReadOnlyDictionary<string, List<Nhl24Slot>> ByFolder { get; }

    public Nhl24Catalog(AssetIndex index)
    {
        Index = index;
        var all = index.Entries.Where(e => e.Kind is AssetKind.Texture or AssetKind.TextureArray).Select(e => new Nhl24Slot(e)).ToList();
        ByName = all.ToDictionary(t => t.Name, StringComparer.Ordinal);
        ByFolder = all.GroupBy(t => Nhl27Catalog.FolderOf(t.Name)).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }
}
