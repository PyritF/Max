using Max.Llm;
using Max.Setup;
using Max.Ui;

namespace Max.Update;

/// <summary>
/// Stille Updates (PLAN.md §5a): Während Max läuft, lädt er im Hintergrund eine neue Version und ein neues
/// Modell. Wird Max geschlossen, pausiert der Download und geht beim nächsten Start weiter.
/// Die neue Exe wird sofort gegen die laufende getauscht (umbenennen geht auch unter Windows), das neue Modell
/// liegt als <see cref="MaxPaths.ModelNext"/> bereit – beides ist ab dem nächsten Start aktiv.
/// </summary>
/// <param name="exePath">Die laufende Exe; null, wenn Max nicht als einzelne Datei läuft (dann nur Modell-Updates).</param>
internal sealed class Updater(HttpClient http, MaxPaths paths, string? exePath, string currentVersion, ModelDownloader? downloader = null)
{
    private readonly ModelDownloader _downloader = downloader ?? new ModelDownloader(http);

    /// <summary>Fortschritt des laufenden Downloads – für /debug.</summary>
    public StepProgress Progress { get; } = new();

    /// <summary>Was gerade passiert, in einem kurzen Satz – für /debug.</summary>
    public string Status { get; private set; } = "noch nicht geprüft";

    /// <summary>Gibt es für diesen Rechner eine neuere Version mit Prüfsumme?</summary>
    public static (string Version, AppAsset Asset)? AppUpdate(Manifest manifest, string currentVersion) =>
        AppVersion.IsNewer(manifest.App.Version, currentVersion)
        && manifest.App.Assets?.GetValueOrDefault(AppVersion.AssetKey) is { Sha256.Length: > 0 } asset
            ? (manifest.App.Version, asset)
            : null;

    /// <summary>Gibt es ein neueres Modell, das noch nicht bereitliegt?</summary>
    public static ModelEntry? ModelUpdate(Manifest manifest, InstallState? installed, InstallState? next) =>
        manifest.Model is { } model && installed is not null && model.Revision > installed.Revision
        && next?.Revision != model.Revision
            ? model
            : null;

    /// <param name="manifest">Schon geladenes Manifest; null = selbst von GitHub holen.</param>
    public async Task RunAsync(Manifest? manifest, CancellationToken ct)
    {
        try
        {
            manifest ??= await ManifestSource.TryLoadRemoteAsync(http, ct);
            if (manifest is null)
            {
                Status = "offline – später wieder";
                return;
            }

            var notes = new List<string>();
            if (exePath is not null && AppUpdate(manifest, currentVersion) is var (version, asset))
            {
                await InstallAppAsync(version, asset, exePath, ct);
                notes.Add($"Version {version} bereit – aktiv beim nächsten Start");
            }
            if (ModelUpdate(manifest, InstallState.Load(paths), InstallState.LoadNext(paths)) is { } model)
            {
                await DownloadModelAsync(model, ct);
                notes.Add("neues Modell bereit – aktiv beim nächsten Start");
            }
            Status = notes.Count > 0 ? string.Join("; ", notes) : "aktuell";
        }
        catch (OperationCanceledException)
        {
            Status += " (pausiert, geht beim nächsten Start weiter)";
        }
        catch (Exception e)
        {
            LlmEngine.Log($"Update fehlgeschlagen: {e.Message}");
            Status = "nicht geklappt – nächster Versuch beim nächsten Start";
        }
    }

    /// <summary>Lädt die neue Exe (fortsetzbar) und tauscht sie gegen die laufende.</summary>
    /// <param name="progress">Sichtbarer Fortschritt (Pflicht-Update beim Start); sonst der für /debug.</param>
    public async Task InstallAppAsync(string version, AppAsset asset, string exe, CancellationToken ct, StepProgress? progress = null)
    {
        Status = $"Version {version} wird geladen";
        var part = Path.Combine(paths.UpdateDir, $"{version}-{asset.File}.part");
        var url = $"{ManifestSource.ReleaseBaseUrl}/v{version}/{asset.File}";
        await _downloader.DownloadAsync(new DownloadTarget(url, asset.Sha256, 0, part, paths.Root), progress ?? Progress, ct);
        ReplaceExecutable(part, exe);
    }

    private async Task DownloadModelAsync(ModelEntry model, CancellationToken ct)
    {
        Status = "neues Modell wird geladen";
        var part = paths.ModelNext + ".part";
        var result = await _downloader.DownloadAsync(new DownloadTarget(model.Url, model.Sha256, model.SizeBytes, part, paths.Root), Progress, ct);
        InstallState.CommitNext(paths, part, model, result, DateTime.Now);
    }

    /// <summary>
    /// Eine laufende Exe lässt sich nicht überschreiben, aber umbenennen: alte zur Seite (<c>.old</c>),
    /// neue an ihren Platz. Klappt das Einsetzen nicht, kommt die alte zurück.
    /// </summary>
    internal static void ReplaceExecutable(string newFile, string exe)
    {
        var old = exe + ".old";
        if (File.Exists(old))
            File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(newFile, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                      | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
