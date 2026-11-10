using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Suite.Core.Frostbite;

/// <summary>
/// Content-addressed prepared texture payloads. The small index also remembers output hashes after a payload is evicted,
/// allowing a repeat Apply to prove that the game's current chunk is already correct without encoding it again.
/// </summary>
public sealed class TexturePayloadCache
{
    const int KeyVersion = 1;
    const long MaxPayloadBytes = 4L * 1024 * 1024 * 1024;

    sealed class Entry
    {
        public string Hash { get; set; } = "";
        public string? Payload { get; set; }
        public long Bytes { get; set; }
        public long LastUsedTicks { get; set; }
    }

    readonly string _dir;
    readonly string _indexPath;
    readonly object _sync = new();
    Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    bool _dirty;

    public TexturePayloadCache(string? directory = null)
    {
        _dir = directory ?? Path.Combine(SuitePaths.Local, "texture-cache");
        _indexPath = Path.Combine(_dir, "index.json");
        try
        {
            if (File.Exists(_indexPath))
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_indexPath)) ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            SuiteLog.Write("texture cache index ignored: " + ex.Message);
            _entries = new(StringComparer.Ordinal);
        }
    }

    /// <summary>A verified cached payload, or null when the hash entry is absent or its payload is unavailable.</summary>
    public byte[]? Load(string key)
    {
        Entry? entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry) || entry.Payload is null) return null;
            entry.LastUsedTicks = DateTime.UtcNow.Ticks;
            _dirty = true;
        }
        try
        {
            var path = Path.Combine(_dir, entry.Payload);
            if (!File.Exists(path)) return null;
            var data = File.ReadAllBytes(path);
            if (data.Length != entry.Bytes || !Convert.ToHexString(SHA256.HashData(data)).Equals(entry.Hash, StringComparison.Ordinal))
            {
                SuiteLog.Write($"texture cache payload failed verification: {entry.Payload}");
                return null;
            }
            return data;
        }
        catch (Exception ex)
        {
            SuiteLog.Write("texture cache read ignored: " + ex.Message);
            return null;
        }
    }

    public string? HashOf(string key)
    {
        lock (_sync) return _entries.TryGetValue(key, out var entry) ? entry.Hash : null;
    }

    /// <summary>Records a result and optionally stores its raw payload for use by another build or after game changes.</summary>
    public void Store(string key, byte[] data, bool keepPayload)
    {
        var hash = Convert.ToHexString(SHA256.HashData(data));
        string? leaf = null;
        if (keepPayload)
        {
            leaf = key + ".bin";
            try
            {
                Directory.CreateDirectory(_dir);
                var tmp = Path.Combine(_dir, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, Path.Combine(_dir, leaf), true);
            }
            catch (Exception ex)
            {
                SuiteLog.Write("texture cache write ignored: " + ex.Message);
                leaf = null;
            }
        }
        lock (_sync)
        {
            var previous = _entries.GetValueOrDefault(key);
            _entries[key] = new Entry
            {
                Hash = hash,
                Payload = leaf ?? previous?.Payload,
                Bytes = leaf is null ? previous?.Bytes ?? 0 : data.Length,
                LastUsedTicks = DateTime.UtcNow.Ticks,
            };
            _dirty = true;
        }
    }

    /// <summary>Persists the hash index and bounds payload storage without discarding the useful hash-only entries.</summary>
    public void Save()
    {
        lock (_sync)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(_dir);
                long total = _entries.Values.Where(e => e.Payload is not null).Sum(e => e.Bytes);
                if (total > MaxPayloadBytes)
                    foreach (var (key, entry) in _entries.Where(e => e.Value.Payload is not null).OrderBy(e => e.Value.LastUsedTicks))
                    {
                        try { File.Delete(Path.Combine(_dir, entry.Payload!)); } catch { }
                        total -= entry.Bytes;
                        entry.Payload = null;
                        entry.Bytes = 0;
                        if (total <= MaxPayloadBytes) break;
                    }
                var tmp = _indexPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries));
                File.Move(tmp, _indexPath, true);
                _dirty = false;
            }
            catch (Exception ex) { SuiteLog.Write("texture cache index save ignored: " + ex.Message); }
        }
    }

    /// <summary>
    /// Hashes only stock data that can affect the output: alpha for replaced slices and complete untouched array slices.
    /// Consequently the key remains stable after Apply, when colour/mip bytes have changed but those inputs have not.
    /// </summary>
    public static string Key(TextureSlots.Slot slot, TextureReplacement replacement, out IReadOnlyList<Rgba>? stockForAlpha)
    {
        var sources = new SortedDictionary<int, string>();
        if (!string.IsNullOrEmpty(replacement.Source)) sources[0] = replacement.Source;
        foreach (var (layer, path) in replacement.Layers ?? []) sources[layer] = path;
        stockForAlpha = replacement.Alpha is null ? null : Ps4Codec.DecodeSlices(slot.Res, slot.Stock);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddText(hash, $"CIMS texture payload v{KeyVersion}");
        AddText(hash, $"{slot.Res.Width}x{slot.Res.Height};{slot.Res.BlockBytes};{slot.Res.Header[12]};mips={string.Join(',', slot.Res.MipSizes)};slices={Ps4Codec.Slices(slot.Res.Header)}");
        AddText(hash, replacement.Alpha ?? "<stock-alpha-not-used>");
        foreach (var (layer, path) in sources)
        {
            bool coefficient = path.StartsWith("coeff:", StringComparison.Ordinal);
            string source = coefficient ? path["coeff:".Length..] : path;
            AddText(hash, $"layer:{layer}:{(coefficient ? "clean-coefficient-v1" : "pixels")}");
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            Add(hash, SHA256.HashData(stream));
        }

        int slices = Ps4Codec.Slices(slot.Res.Header);
        for (int slice = 0; slice < slices; slice++)
        {
            if (!sources.ContainsKey(slice))
            {
                AddText(hash, $"untouched:{slice}");
                for (int mip = 0; mip < slot.Res.MipCount; mip++)
                    Add(hash, slot.Stock.AsSpan(Ps4Codec.Offset(slot.Res, slices, slice, mip), slot.Res.MipSizes[mip]));
            }
            else if (stockForAlpha is not null)
            {
                AddText(hash, $"alpha:{slice}");
                var pixels = stockForAlpha[slice].Pixels;
                var alpha = new byte[pixels.Length / 4];
                for (int i = 0; i < alpha.Length; i++) alpha[i] = pixels[i * 4 + 3];
                Add(hash, alpha);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    static void AddText(IncrementalHash hash, string value) => Add(hash, Encoding.UTF8.GetBytes(value));

    static void Add(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
