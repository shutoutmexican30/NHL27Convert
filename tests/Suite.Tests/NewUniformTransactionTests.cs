using Suite.Convert27;

namespace Suite.Tests;

public sealed class NewUniformTransactionTests
{
    [Fact]
    public void Assets_and_databases_read_back_without_restore_on_success()
    {
        var events = new List<string>();
        NewUniformTransaction.Run("backup",
            () => events.Add("assets"), () => events.Add("databases"),
            () => events.Add("read-back"), () => events.Add("restore"));
        Assert.Equal(["assets", "databases", "read-back"], events);
    }

    [Fact]
    public void Database_failure_restores_assets_and_databases_together()
    {
        var events = new List<string>();
        var ex = Assert.Throws<InvalidOperationException>(() => NewUniformTransaction.Run("backup",
            () => events.Add("assets"),
            () => { events.Add("databases"); throw new IOException("DB write failed"); },
            () => events.Add("read-back"),
            () => events.Add("restored-and-verified")));
        Assert.Equal(["assets", "databases", "restored-and-verified"], events);
        Assert.Contains("restored together and verified", ex.Message, StringComparison.Ordinal);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public void Read_back_failure_also_restores_and_a_rollback_failure_is_not_hidden()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NewUniformTransaction.Run("backup",
            () => { }, () => { }, () => throw new InvalidDataException("wrong bytes"), () => { }));
        Assert.Contains("wrong bytes", ex.Message, StringComparison.Ordinal);

        var aggregate = Assert.Throws<AggregateException>(() => NewUniformTransaction.Run("backup",
            () => { }, () => throw new IOException("write"), () => { }, () => throw new IOException("restore")));
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains(aggregate.InnerExceptions, e => e.Message == "restore");
    }
}
