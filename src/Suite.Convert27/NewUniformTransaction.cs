namespace Suite.Convert27;

/// <summary>The ordered asset/database transaction used by <see cref="NewUniformApply"/>; kept separate for rollback tests.</summary>
public static class NewUniformTransaction
{
    public static void Run(
        string backup,
        Action writeAssets,
        Action writeDatabases,
        Action readBack,
        Action restoreAndVerify)
    {
        try
        {
            writeAssets();
            writeDatabases();
            readBack();
        }
        catch (Exception applyError)
        {
            try { restoreAndVerify(); }
            catch (Exception rollbackError)
            {
                throw new AggregateException($"New-uniform apply failed and rollback also failed. Backup: {backup}", applyError, rollbackError);
            }
            throw new InvalidOperationException(
                $"New-uniform apply failed ({applyError.Message}); assets and databases were restored together and verified. Backup: {backup}", applyError);
        }
    }
}
