namespace Suite.Core;

/// <summary>
/// Command-line tools that can write a game only do so when the command says <c>--apply</c> and names a target with
/// <c>--target</c>; anything else (a missing or mistyped mode included) is a dry run. Learned the hard way: a mode that
/// was meant to be read-only did not exist yet, and the default path wrote a game.
/// </summary>
public static class WriteGate
{
    public static bool Allowed(IReadOnlyList<string> args)
    {
        int t = args.ToList().IndexOf("--target");
        return args.Contains("--apply") && t >= 0 && t + 1 < args.Count && !string.IsNullOrWhiteSpace(args[t + 1]) && !args[t + 1].StartsWith("--");
    }
}
