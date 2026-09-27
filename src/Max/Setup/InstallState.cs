using System.Text.Json;

namespace Max.Setup;

/// <summary>Was installiert ist – steht in <c>state.json</c> im Datenordner.</summary>
/// <param name="Model">Dateiname des Modells, z. B. "Qwen3.5-9B-Q4_K_M.gguf".</param>
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
