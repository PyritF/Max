using Max.Setup;

namespace Max.Update;

/// <summary>Beim Start, bevor irgendetwas geladen ist: bereitliegende Updates aktivieren und Reste aufräumen.</summary>
internal static class UpdateApplier
{
    /// <returns>true, wenn ein neues Modell eingesetzt wurde.</returns>
    public static bool Apply(MaxPaths paths, string? exePath)
    {
        // Die alte Exe vom letzten Tausch – jetzt läuft sie nicht mehr.
        if (exePath is not null)
            TryDelete(exePath + ".old");

        // Neues Modell: erst jetzt tauschen, vorher war core.bin geladen und gesperrt.
        if (!File.Exists(paths.ModelNext) || InstallState.LoadNext(paths) is null)
            return false;
        File.Move(paths.ModelNext, paths.Model, overwrite: true);
        File.Move(paths.StateNext, paths.State, overwrite: true);
        return true;
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Noch gesperrt – dann eben beim nächsten Mal.
        }
    }
}
