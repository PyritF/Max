using System.Text.Json;

namespace Max.Setup;

/// <summary>Einstellungen, die man in Max selbst ändert – stehen in <c>settings.json</c> im Datenordner.</summary>
/// <param name="Thinking">Denkt Max vor jeder Antwort nach? (/denken)</param>
internal sealed record Settings(bool Thinking = true)
{
    /// <summary>Liest die Einstellungen; fehlt die Datei oder ist sie kaputt, gelten die Standardwerte.</summary>
    public static Settings Load(MaxPaths paths)
    {
        try
        {
            if (File.Exists(paths.Settings))
                return JsonSerializer.Deserialize(File.ReadAllText(paths.Settings), SetupJson.Default.Settings) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new Settings();
    }

    public void Save(MaxPaths paths)
    {
        paths.EnsureExists();
        var temp = paths.Settings + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, SetupJson.Default.Settings));
        File.Move(temp, paths.Settings, overwrite: true);
    }
}
