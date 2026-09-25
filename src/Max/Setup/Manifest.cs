using System.Text.Json;
using System.Text.Json.Serialization;

namespace Max.Setup;

/// <summary>
/// Steuert App- und Modell-Updates (PLAN.md §4). Liegt als <c>manifest.json</c> im Repo;
/// eine Kopie ist in die Exe eingebaut.
/// </summary>
internal sealed record Manifest(AppInfo App, Dictionary<string, TierEntry> Tiers)
{
    public TierEntry? For(Tier tier) => Tiers.GetValueOrDefault(tier.ToString());

    /// <summary>Taugt das Manifest? Alle Stufen müssen eine Adresse haben.</summary>
    public bool IsComplete => Enum.GetValues<Tier>().All(t => For(t) is { Url.Length: > 0 });
}

internal sealed record AppInfo(
    string Version,
    string? MinVersion = null,
    bool Disabled = false,
    string? Message = null,
    Dictionary<string, AppAsset>? Assets = null);

internal sealed record AppAsset(string File, string? Sha256 = null);

/// <param name="Revision">Wird erhöht, wenn es für die Stufe ein neues Modell gibt.</param>
/// <param name="Url">Download-Adresse der GGUF-Datei.</param>
/// <param name="Sha256">
/// Erwartete Prüfsumme. Fehlt sie, nimmt Max die, die Hugging Face beim Download mitliefert.
/// </param>
/// <param name="SizeBytes">Ungefähre Größe – für die Speicherplatz-Prüfung, bevor der Download startet.</param>
/// <param name="ContextSize">Wie viele Tokens das Modell auf einmal sieht.</param>
internal sealed record TierEntry(int Revision, string Url, string? Sha256, long SizeBytes, int ContextSize);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(InstallState))]
internal sealed partial class SetupJson : JsonSerializerContext;

/// <summary>Holt das Manifest – aus dem Netz, sonst aus der Exe.</summary>
internal static class ManifestSource
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/PyritF/Max/main/manifest.json";
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Versucht kurz die aktuelle Fassung von GitHub zu laden. Klappt das nicht (offline, Timeout,
    /// kaputte Datei), gilt die eingebaute Kopie. <c>MAX_MANIFEST_URL</c> verlegt die Adresse (Tests).
    /// </summary>
    public static async Task<Manifest> LoadAsync(HttpClient http, CancellationToken ct, string? url = null)
    {
        url ??= Environment.GetEnvironmentVariable("MAX_MANIFEST_URL") ?? RemoteUrl;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RemoteTimeout);

        try
        {
            using var response = await http.GetAsync(url, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var remote = await JsonSerializer.DeserializeAsync(stream, SetupJson.Default.Manifest, timeout.Token);
                if (remote is { IsComplete: true })
                    return remote;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Netzfehler, Timeout oder ungültiges JSON: kein Drama, es gibt ja die eingebaute Kopie.
        }

        return Embedded();
    }

    public static Manifest Embedded()
    {
        using var stream = typeof(ManifestSource).Assembly.GetManifestResourceStream("manifest.json")
            ?? throw new InvalidOperationException("manifest.json fehlt in der Exe.");
        return JsonSerializer.Deserialize(stream, SetupJson.Default.Manifest)
            ?? throw new InvalidOperationException("manifest.json ist leer.");
    }
}
