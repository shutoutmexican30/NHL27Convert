using System.Buffers.Binary;
using Suite.Convert27;
using Suite.Core.Frostbite;
using Xunit;

namespace Suite.Tests;

public class KitCasRecordTests
{
    [Fact]
    public void Record_with_entries_in_two_cas_files_reads_back_through_the_toc_record_parser()
    {
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 0x20);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), 0x20);
        const uint kit = 0x0000_0511, stock = 0x0000_0505; // package 05: new kit cas_17, stock cas_05
        var entries = new List<(uint, uint, uint)> { (kit, 10, 100), (stock, 2000, 30), (stock, 2100, 40), (kit, 110, 50) };
        var record = FullKitInstall.BuildRecord(header, entries);

        int flagsAt = (int)BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(8));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(12)));
        Assert.Equal([1, 1, 0, 1], record[flagsAt..]);
        // Walk it the way BundleIndex.Records does: a location word only where the flag is 1.
        int at = 0x20; uint word = 0;
        for (int k = 0; k < 4; k++)
        {
            if (record[flagsAt + k] == 1) { word = BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(at)); at += 4; }
            Assert.Equal(entries[k].Item1, word);
            Assert.Equal(entries[k].Item2, BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(at)));
            Assert.Equal(entries[k].Item3, BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(at + 4)));
            at += 8;
        }
        Assert.Equal(flagsAt, at);
    }
}
