using Suite.Core;
using Xunit;

namespace Suite.Rosters.Tests;

public class WriteGateTests
{
    [Theory]
    [InlineData(new[] { "--target", "D:/game", "--apply" }, true)]
    [InlineData(new[] { "--apply", "--target", "D:/game" }, true)]
    [InlineData(new[] { "--target", "D:/game" }, false)]                      // no --apply: dry run
    [InlineData(new[] { "--target", "D:/game", "--show", "9490" }, false)]    // an unknown mode never writes
    [InlineData(new[] { "--apply" }, false)]                                  // no target
    [InlineData(new[] { "--apply", "--target", "--restore-check" }, false)]   // target missing its value
    public void Writes_only_with_apply_and_a_target(string[] args, bool allowed) => Assert.Equal(allowed, WriteGate.Allowed(args));
}
