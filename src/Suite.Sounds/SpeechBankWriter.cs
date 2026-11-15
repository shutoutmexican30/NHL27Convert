using System.Buffers.Binary;

namespace Suite.Sounds;

/// <summary>
/// Rebuilds one speech wave (its chunk, its SBle bank RES and its NewWaveAsset EBX) with some takes replaced. Pure:
/// works on byte arrays, never on a game. Replacement streams are appended to the end of the chunk (4-byte aligned,
/// like EA's own packing) and the replaced rows' offset and duration are repointed. Old audio stays in place unused, so
/// every other take keeps its exact bytes and offset. With no replacements the output is byte-identical to the input.
/// <para>Columns are rewritten in place, so the RES keeps its size: a stored value must fit the column's width
/// (offset columns in the eng_inxp surname waves are 4 bytes), and a constant column (width 0) can only change
/// when the table has one row (then the descriptor's param is rewritten).</para>
/// </summary>
public static class SpeechBankWriter
{
    public sealed record Replacement(byte[] Sps, double Seconds);
    public sealed record Result(byte[] Chunk, byte[] Res, byte[] Ebx, int Replaced);

    public static Result Rebuild(byte[] chunk, byte[] res, byte[] ebx, IReadOnlyDictionary<uint, Replacement> replacements)
    {
        var bank = HarmonyBank.Parse(res);
        var clips = bank[HarmonyBank.Clips] ?? throw new InvalidDataException("No per-clip table.");
        var streams = bank[HarmonyBank.Streams] ?? throw new InvalidDataException("No stream table.");
        var ids = clips[HarmonyBank.ColVariation] ?? throw new InvalidDataException("No VariationId column.");
        var off = streams[HarmonyBank.ColOffset] ?? throw new InvalidDataException("No offset column.");
        var dur = streams[HarmonyBank.ColDuration];
        if (clips.Rows != streams.Rows) throw new InvalidDataException("Clip and stream tables differ in rows.");
        var rows = Enumerable.Range(0, clips.Rows).Where(r => replacements.ContainsKey((uint)ids.Long(r))).ToList();
        if (rows.Count == 0) return new(chunk, res, ebx, 0);

        var newRes = (byte[])res.Clone();
        using var ms = new MemoryStream();
        ms.Write(chunk);
        foreach (int r in rows)
        {
            var rep = replacements[(uint)ids.Long(r)];
            if (SpsStream.TryRead(rep.Sps, 0) is not { } s || s.Length != rep.Sps.Length) throw new InvalidDataException("Replacement is not exactly one SPS stream.");
            while (ms.Length % 4 != 0) ms.WriteByte(0);
            long at = ms.Length;
            ms.Write(rep.Sps);
            SetInt(newRes, off, r, streams.Rows, at + 3);
            if (dur is not null) SetFloat(newRes, dur, r, streams.Rows, rep.Seconds);
        }
        while (ms.Length % 4 != 0) ms.WriteByte(0);
        var newChunk = ms.ToArray();

        // Chunk size: the bank's one-row table and the EBX's SoundDataChunk.ChunkSize.
        if (bank[HarmonyBank.ChunkInfo]?[HarmonyBank.ColChunkSize] is { } cs) SetInt(newRes, cs, 0, 1, newChunk.Length);
        var newEbx = (byte[])ebx.Clone();
        var fields = SpeechCatalog.ChunkFields(newEbx);
        if (fields.Count != 1) throw new InvalidDataException($"Wave EBX lists {fields.Count} chunks; expected 1.");
        BinaryPrimitives.WriteUInt32LittleEndian(newEbx.AsSpan(fields[0] + 16), checked((uint)newChunk.Length));
        return new(newChunk, newRes, newEbx, rows.Count);
    }

    static void SetInt(byte[] b, HarmonyBank.Column c, int row, int rows, long value)
    {
        if (c.Width == 0)
        {
            if (rows != 1) throw new InvalidOperationException($"Column {c.Tag:X8} is constant across {rows} rows.");
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(c.DescriptorAt + 8), checked((uint)value));
            return;
        }
        long stored = value - c.Param;
        long max = c.Signed ? (1L << (c.Width * 8 - 1)) - 1 : (c.Width >= 8 ? long.MaxValue : (1L << (c.Width * 8)) - 1);
        long min = c.Signed ? -(1L << (c.Width * 8 - 1)) : 0;
        if (stored < min || stored > max) throw new InvalidOperationException($"Value {value} does not fit column {c.Tag:X8} (width {c.Width}, param {c.Param}).");
        for (int i = 0; i < c.Width; i++) b[c.DataAt + row * c.Width + i] = (byte)(stored >> (8 * i));
    }

    static void SetFloat(byte[] b, HarmonyBank.Column c, int row, int rows, double seconds)
    {
        if (c.Kind != 5) throw new InvalidOperationException($"Column {c.Tag:X8} is not a float column.");
        if (c.Width == 0) { if (rows == 1) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(c.DescriptorAt + 8), (float)seconds); return; }
        double baseValue = BitConverter.Int32BitsToSingle((int)c.Param);
        long max = (1L << (c.Width * 8)) - 1;
        long stored = Math.Clamp((long)Math.Round((seconds - baseValue) * 33554432.0), 0, max);
        for (int i = 0; i < c.Width; i++) b[c.DataAt + row * c.Width + i] = (byte)(stored >> (8 * i));
    }
}
