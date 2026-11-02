using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Suite.Core;

namespace Suite.Convert27;

/// <summary>
/// Keeps the stopgap NHL24Kit Worker from undoing the suite's own writes. The Worker restores whatever it last applied
/// before every apply and on restore. If it believes a game is modded but the suite never applied through it (a run was
/// stopped, or another tool used it), that rollback would remove rosters / ratings / portraits the suite wrote itself,
/// so Apply and Restore refuse until it is sorted out. The suite records its own Worker applies in
/// %LOCALAPPDATA%\CenterIceModdingSuite\backups\worker-applied-&lt;target hash&gt;.txt.
/// </summary>
public static class WorkerGuard
{
    static string Marker(string target) => Path.Combine(SuitePaths.Ensure(Path.Combine(SuitePaths.Local, "backups")),
        "worker-applied-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(target.ToLowerInvariant())))[..12] + ".txt");

    public static async Task<string?> ProblemAsync(SuiteSettings s, string target, CancellationToken ct = default)
    {
        bool modded;
        try
        {
            using var doc = JsonDocument.Parse(await Worker.RunAsync(s, ["status", target], ct));
            modded = doc.RootElement.TryGetProperty("modded", out var m) && m.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex)
        {
            return "Could not read the NHL24Kit Worker's state for this game: " + ex.Message;
        }
        if (!modded || File.Exists(Marker(target))) return null;
        return "Refused: the NHL24Kit Worker has a leftover record of changes to this game that the suite did not make " +
               "(for example a Worker run that was stopped). Applying or restoring through it now would first roll that record " +
               "back and remove rosters, ratings or portraits the suite wrote with its own writer. Nothing was changed.";
    }

    public static void MarkApplied(string target) => File.WriteAllText(Marker(target), DateTime.Now.ToString("O"));
    public static void ClearMark(string target) { try { File.Delete(Marker(target)); } catch (IOException) { } }
}
