using System.Text.Json;

namespace Max.Setup;

/// <summary>Was installiert ist – steht in <c>state.json</c> im Datenordner.</summary>
/// <param name="Model">Dateiname des Modells. Fehlt bei Installationen aus der Zeit der Stufen (S, M, L, XL).</param>
/// <param name="ContextSize">Aus dem Manifest; ältere state.json haben den Wert noch nicht.</param>
internal sealed record InstallState(string? Model, int Revision, string Sha256, long SizeBytes, DateTime InstalledAt, int ContextSize = InstallState.DefaultContextSize)
{
    public const int DefaultContextSize = 8192;

    public static InstallState? Load(MaxPaths paths)
    {
        try
        {
            if (!File.Exists(paths.State))
                return null;
            var json = File.ReadAllText(paths.State);
            return JsonSerializer.Deserialize(json, SetupJson.Default.InstallState);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // kaputt oder unlesbar → wie nicht installiert
        }
    }

    /// <summary>
    /// Modell vorhanden und passend zu state.json? Die Prüfsumme wird hier bewusst nicht neu
    /// berechnet (mehrere GB) – der Start soll schnell bleiben. Die Größe reicht als Plausibilitätsprüfung.
    /// </summary>
    public static bool IsInstalled(MaxPaths paths)
    {
        var state = Load(paths);
        return state is { Model: not null }
               && File.Exists(paths.Model)
               && new FileInfo(paths.Model).Length == state.SizeBytes;
    }

    /// <summary>
    /// Räumt Reste aus der Zeit der Stufen weg: ein kleineres Modell (state.json ohne Modellnamen), einen
    /// angefangenen Download dazu und beiseitegelegte Modelle. Danach richtet sich Max mit dem aktuellen Modell neu ein.
    /// </summary>
    /// <returns>true, wenn etwas entfernt wurde.</returns>
    public static bool RemoveOutdated(MaxPaths paths)
    {
        var removed = false;
        if (File.Exists(paths.State) && Load(paths) is not { Model: not null })
        {
            foreach (var file in new[] { paths.Model, paths.ModelPart, paths.State })
                removed |= TryDelete(file);
        }
        var shelf = Path.Combine(paths.Root, "models");
        if (Directory.Exists(shelf))
        {
            Directory.Delete(shelf, recursive: true);
            removed = true;
        }
        return removed;
    }

    private static bool TryDelete(string file)
    {
        if (!File.Exists(file))
            return false;
        File.Delete(file);
        return true;
    }

    /// <summary>Macht aus dem fertig geprüften Download das Modell und merkt sich, was installiert ist.</summary>
    public static InstallState Commit(MaxPaths paths, ModelEntry entry, DownloadResult download, DateTime now)
    {
        File.Move(paths.ModelPart, paths.Model, overwrite: true);

        var state = new InstallState(entry.FileName, entry.Revision, download.Sha256, download.SizeBytes, now, entry.ContextSize);
        // Erst in eine Hilfsdatei schreiben, dann umbenennen: So ist state.json nie halb geschrieben.
        var temp = paths.State + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, SetupJson.Default.InstallState));
        File.Move(temp, paths.State, overwrite: true);
        return state;
    }
}
