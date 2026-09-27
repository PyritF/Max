using Max.Setup;
using Max.Ui;

namespace Max.Update;

/// <summary>Der Schritt „Suche nach Updates“ beim Start: Not-Aus, Pflicht-Update, Hinweis auf eine neue Version.</summary>
internal static class StartupUpdate
{
    /// <returns>Kurzer Text für die Statuszeile.</returns>
    /// <exception cref="SetupException">Max darf nicht starten (abgeschaltet, zu alt) – die Meldung steht dann auf dem Bildschirm.</exception>
    public static async Task<string> CheckAsync(Manifest manifest, Updater updater, string current, string? exe, StepProgress progress, CancellationToken ct)
    {
        if (manifest.App.Disabled)
            throw new SetupException(manifest.App.Message ?? "Max ist gerade abgeschaltet. Versuch es später noch einmal.");

        var update = Updater.AppUpdate(manifest, current);
        if (AppVersion.IsNewer(manifest.App.MinVersion, current))
        {
            // Pflicht-Update: ausnahmsweise mit sichtbarem Fortschritt, danach neu starten.
            if (exe is null || update is not var (required, asset))
                throw new SetupException("Diese Version von Max ist zu alt. Lade die neue von GitHub herunter.");
            await updater.InstallAppAsync(required, asset, exe, ct, progress);
            throw new SetupException($"Max ist jetzt auf Version {required}. Starte ihn einfach neu.");
        }

        return update is var (version, _) && exe is not null ? $"Version {version} kommt" : "aktuell";
    }
}
