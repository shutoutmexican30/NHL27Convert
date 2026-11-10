using Xunit;

namespace Suite.Rosters.Tests;

public sealed class T3CsvMetadataTests
{
    [Fact]
    public void Reads_the_NHL27_name_key_as_well_as_the_NHL24_table_key()
    {
        string dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "demo.csv"), "a,b\r\n1,2\r\n");
            File.WriteAllText(Path.Combine(dir, "tables.json"), "[{\"name\":\"demo\",\"valid\":1}]");
            var table = T3Csv.Load(dir, "demo");
            Assert.Equal(1, table.ValidCount);
            Assert.Equal("2", table.Get(table.Rows[0], "b"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
