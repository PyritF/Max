using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Max.Llm;

/// <summary>
/// Hebt den gerechneten Anfang des System-Prompts auf der Platte auf. Ohne Grafikkarte dauert das Rechnen
/// beim Start bis zu zwei Minuten, das Laden eine Sekunde. Der Schlüssel enthält Modell, Einstellungen und
/// die Tokens selbst: Ändert sich eines davon (neue Version, anderes Modell), wird neu gerechnet und überschrieben.
/// </summary>
/// <param name="identity">Modell und Engine-Einstellungen, z. B. Prüfsumme + <see cref="LlmEngine.StateIdentity"/>.</param>
internal sealed class PromptCache(string directory, string identity)
{
    private string StatePath => Path.Combine(directory, "prompt.state");
    private string MetaPath => Path.Combine(directory, "prompt.json");

    /// <summary>Lädt den Stand, wenn er zu genau diesen Tokens gehört.</summary>
    public bool TryLoad(ILanguageModel model, IReadOnlyList<int> tokens)
    {
        try
        {
            if (!File.Exists(MetaPath) || !File.Exists(StatePath))
                return false;
            var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath));
            if (meta?.Key != Key(tokens))
                return false;
            return model.LoadState(StatePath, tokens);
        }
        catch (Exception e)
        {
            LlmEngine.Log($"Gespeicherter System-Prompt nicht lesbar: {e.Message}");
            return false;
        }
    }

    /// <summary>Speichert den aktuellen Stand – er muss genau <paramref name="tokens"/> enthalten.</summary>
    public void Save(ILanguageModel model, IReadOnlyList<int> tokens)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.Delete(MetaPath);                         // erst die Beschreibung weg: nie ein halber Stand mit gültigem Schlüssel
            if (model.SaveState(StatePath))
                File.WriteAllText(MetaPath, JsonSerializer.Serialize(new Meta(Key(tokens), tokens.Count)));
        }
        catch (Exception e)
        {
            LlmEngine.Log($"System-Prompt konnte nicht gespeichert werden: {e.Message}");
        }
    }

    internal string Key(IReadOnlyList<int> tokens) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "|" + string.Join(',', tokens))));

    private sealed record Meta(string Key, int Tokens);
}
