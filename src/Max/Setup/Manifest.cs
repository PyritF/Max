using System.Text.Json;
using System.Text.Json.Serialization;

namespace Max.Setup;

/// <summary>
/// Steuert App- und Modell-Updates (PLAN.md §4). Liegt als <c>manifest.json</c> im Repo;
/// eine Kopie ist in die Exe eingebaut.
/// </summary>
internal sealed record Manifest(AppInfo App, ModelEntry? Model)
{
    /// <summary>Taugt das Manifest? Das Modell muss eine Adresse haben (ältere Fassungen mit Stufen fallen durch).</summary>
    public bool IsComplete => Model is { Url.Length: > 0 };
}

internal sealed record AppInfo(
    string Version,
    string? MinVersion = null,
    bool Disabled = false,
    string? Message = null,
    Dictionary<string, AppAsset>? Assets = null);

internal sealed record AppAsset(string File, string? Sha256 = null);

/// <param name="Revision">Wird erhöht, wenn es ein neues Modell gibt.</param>
/// <param name="Url">Download-Adresse der GGUF-Datei.</param>
/// <param name="Sha256">
/// Erwartete Prüfsumme. Fehlt sie, nimmt Max die, die Hugging Face beim Download mitliefert.
/// </param>
/// <param name="SizeBytes">Ungefähre Größe – für die Speicherplatz-Prüfung, bevor der Download startet.</param>
/// <param name="ContextSize">Wie viele Tokens das Modell auf einmal sieht.</param>
/// <param name="ThinkingBudget">Höchstens so viele Tokens Nachdenken vor einer Antwort.</param>
internal sealed record ModelEntry(int Revision, string Url, string? Sha256, long SizeBytes, int ContextSize, int ThinkingBudget = 512)
{
    /// <summary>Dateiname aus der Adresse, z. B. "Qwen3.5-9B-Q4_K_M.gguf" – kennzeichnet, welches Modell installiert ist.</summary>
    public string FileName => Uri.UnescapeDataString(Url[(Url.LastIndexOf('/') + 1)..]);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(InstallState))]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SetupJson : JsonSerializerContext;

/// <summary>Holt das Manifest – aus dem Netz, sonst aus der Exe.</summary>
internal static class ManifestSource
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/PyritF/Max/main/manifest.json";

    /// <summary>Hier liegen die Exe-Dateien jeder Version: <c>…/v0.2.0/max.exe</c>. <c>MAX_RELEASE_URL</c> verlegt die Adresse (Tests).</summary>
    public static string ReleaseBaseUrl =>
        Environment.GetEnvironmentVariable("MAX_RELEASE_URL") ?? "https://github.com/PyritF/Max/releases/download";
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Versucht kurz die aktuelle Fassung von GitHub zu laden. Klappt das nicht (offline, Timeout,
    /// kaputte Datei), gilt die eingebaute Kopie. <c>MAX_MANIFEST_URL</c> verlegt die Adresse (Tests).
    /// </summary>
    public static async Task<Manifest> LoadAsync(HttpClient http, CancellationToken ct, string? url = null) =>
        await TryLoadRemoteAsync(http, ct, url) ?? Embedded();

    /// <summary>Die aktuelle Fassung von GitHub – oder null (offline, Timeout, kaputte Datei).</summary>
    public static async Task<Manifest?> TryLoadRemoteAsync(HttpClient http, CancellationToken ct, string? url = null)
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

        return null;
    }

    public static Manifest Embedded()
    {
        using var stream = typeof(ManifestSource).Assembly.GetManifestResourceStream("manifest.json")
            ?? throw new InvalidOperationException("manifest.json fehlt in der Exe.");
        return JsonSerializer.Deserialize(stream, SetupJson.Default.Manifest) is { IsComplete: true } manifest
            ? manifest
            : throw new InvalidOperationException("manifest.json ist unvollständig.");
    }
}
