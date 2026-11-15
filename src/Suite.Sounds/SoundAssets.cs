using Suite.Core.Frostbite;

namespace Suite.Sounds;

/// <summary>Where the speech readers get their bytes: decoded EBX/RES by asset name and decoded chunks by id.</summary>
public interface ISoundAssets
{
    IEnumerable<string> EbxNames { get; }
    byte[]? Ebx(string name);
    byte[]? Res(string name);
    byte[]? Chunk(Guid id);
}

/// <summary>NHL 24 (PS4 build) assets through Suite.Core's <see cref="BundleIndex"/> and TOC chunk tables. Read only.</summary>
public sealed class Nhl24SoundAssets : ISoundAssets
{
    readonly BundleIndex _index;
    readonly string _game;
    List<TocFile>? _tocs;

    public Nhl24SoundAssets(BundleIndex index) { _index = index; _game = index.Game; }

    public BundleIndex Index => _index;
    public IEnumerable<string> EbxNames => _index.EbxByName.Keys;

    public byte[]? Ebx(string name) =>
        _index.EbxByName.TryGetValue(name, out var l) && l.Count > 0 ? Decode(BundleIndex.ReadBytes(Latest(l, e => e.Data).Data)) : null;

    public byte[]? Res(string name) =>
        _index.ResByName.TryGetValue(name, out var l) && l.Count > 0 ? Decode(BundleIndex.ReadBytes(Latest(l, r => r.Data).Data)) : null;

    public byte[]? Chunk(Guid id)
    {
        _tocs ??= TocChunkWriter.LoadTocs(_game);
        var r = TocChunkWriter.FindAll(_tocs, id).FirstOrDefault();
        if (r is not null) return TocChunkWriter.ReadRaw(_game, r.Where);
        return _index.ChunksById.TryGetValue(id, out var c) && c.Count > 0 ? Decode(BundleIndex.ReadBytes(c[0].Data)) : null;
    }

    /// <summary>The update-layer copy when there is one (it carries the LCU additions), else the last copy.</summary>
    static T Latest<T>(List<T> copies, Func<T, BundleIndex.Location> at) =>
        copies.LastOrDefault(c => at(c).Toc.Contains(Path.DirectorySeparatorChar + "Patch" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ?? copies[^1];

    public static byte[] Decode(byte[] stored)
    {
        try { return ChunkBlocks.Parse(stored).Decode(); } catch (InvalidDataException) { return stored; }
    }
}
