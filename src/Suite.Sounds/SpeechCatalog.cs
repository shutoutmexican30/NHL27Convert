using System.Buffers.Binary;

namespace Suite.Sounds;

/// <summary>
/// Read-only index of main-commentary name calls (NHL 24 PS4 and NHL 27 PS5 use the same classes). How a spoken
/// surname is stored:
/// <code>
/// player (bio audioid) --code: PG_*_surname_id--> surname keyword (e.g. NHL 24 Stamkos = 1091)
/// keyword --SentenceKeywords--> VariationIds (one per take, per phrase-position wave)
/// VariationId --wave RES (SBle)--> stream offset + duration inside that NewWaveAsset's single chunk
/// VariationId --transcription collection--> text ("Stamkos")
/// </code>
/// The audioid → keyword step happens in the executable; it is not in the DB or any speech asset.
/// </summary>
public sealed class SpeechCatalog
{
    public const string Root = "sound/speech/maincommentary/";
    public const string TranscriptionsAsset = Root + "database/maincommentarysentencetranscriptioncollection";
    public const string KeywordsAsset = Root + "database/maincommentarykeywords";

    public sealed record Clip(string Wave, uint VariationId, int Offset, int Length, double Seconds, int[] Keywords, string Text);
    public sealed record Wave(string Name, Guid ChunkId, uint ChunkSize, HarmonyBank Bank, byte[] Ebx, byte[] Res);

    public ISoundAssets Assets { get; }
    public Dictionary<uint, string> Transcriptions { get; }
    public Dictionary<uint, int[]> Keywords { get; }
    public IReadOnlyList<Wave> Waves { get; }
    public IReadOnlyList<Clip> Clips { get; }

    SpeechCatalog(ISoundAssets a, Dictionary<uint, string> t, Dictionary<uint, int[]> k, List<Wave> w, List<Clip> c)
    { Assets = a; Transcriptions = t; Keywords = k; Waves = w; Clips = c; }

    /// <summary>Loads the speech DB plus every main-commentary wave whose name matches <paramref name="waveFilter"/>.</summary>
    public static SpeechCatalog Load(ISoundAssets assets, Func<string, bool> waveFilter)
    {
        var transcriptions = SpeechDatabase.ReadTranscriptions(assets.Ebx(TranscriptionsAsset) ?? throw Missing(TranscriptionsAsset));
        var keywords = SpeechDatabase.ReadKeywords(assets.Ebx(KeywordsAsset) ?? throw Missing(KeywordsAsset));
        var waves = new List<Wave>();
        var clips = new List<Clip>();
        foreach (var name in assets.EbxNames.Where(n => n.StartsWith(Root + "soundwaves/", StringComparison.Ordinal) && waveFilter(n)).Order(StringComparer.Ordinal))
        {
            var res = assets.Res(name);
            if (res is null || res.Length < 4 || res[0] != (byte)'S' || res[1] != (byte)'B') continue;
            var ebx = assets.Ebx(name)!;
            var chunks = ReadChunks(ebx);
            if (chunks.Count == 0) continue;           // stub waves outside the language folders carry no audio
            var bank = HarmonyBank.Parse(res);
            var (id, size) = chunks[0];
            waves.Add(new(name, id, size, bank, ebx, res));
            var list = bank.SpeechClips().OrderBy(c => c.Offset).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var (vid, off, sec) = list[i];
                int next = i + 1 < list.Count ? list[i + 1].Offset : (int)size;
                clips.Add(new(name, vid, off, next - off, sec, keywords.GetValueOrDefault(vid, []),
                    SpeechDatabase.CleanText(transcriptions.GetValueOrDefault(vid, ""))));
            }
        }
        return new SpeechCatalog(assets, transcriptions, keywords, waves, clips);
    }

    public IEnumerable<Clip> ByKeyword(int keyword) => Clips.Where(c => c.Keywords.Contains(keyword));

    /// <summary>Takes whose transcription is the surname, alone or with its phrase words ("to X", "by X", "X.").</summary>
    public IEnumerable<Clip> ByText(string surname, bool phrases = false) => Clips.Where(c =>
        string.Equals(c.Text, surname, StringComparison.OrdinalIgnoreCase)
        || (phrases && Words(c.Text).Contains(surname, StringComparer.OrdinalIgnoreCase)));

    static string[] Words(string text) => text.Split([' ', '.', ',', '/', '\\', '!', '?'], StringSplitOptions.RemoveEmptyEntries);

    public byte[] ChunkOf(Wave w) => Assets.Chunk(w.ChunkId) ?? throw new InvalidDataException($"chunk {w.ChunkId} of {w.Name} not found");

    /// <summary>SoundDataAsset.Chunks (root +0x20): List&lt;SoundDataChunk&gt; of 20-byte {Guid ChunkId, u32 ChunkSize}.</summary>
    public static List<(Guid Id, uint Size)> ReadChunks(byte[] ebx) =>
        ChunkFields(ebx).Select(at => (new Guid(ebx.AsSpan(at, 16)), BinaryPrimitives.ReadUInt32LittleEndian(ebx.AsSpan(at + 16)))).ToList();

    /// <summary>File offsets of each SoundDataChunk record in a wave EBX (GUID at +0, u32 size at +16).</summary>
    public static List<int> ChunkFields(byte[] ebx)
    {
        const int field = 0x30 + 0x20;
        int rel = BinaryPrimitives.ReadInt32LittleEndian(ebx.AsSpan(field));
        if (rel == 0) return [];
        int at = field + rel, n = BinaryPrimitives.ReadInt32LittleEndian(ebx.AsSpan(at - 4));
        return Enumerable.Range(0, n).Select(i => at + 20 * i).ToList();
    }

    static InvalidDataException Missing(string name) => new($"EBX not found: {name}");
}
