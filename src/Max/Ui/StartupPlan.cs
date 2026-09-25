using Max.Setup;

namespace Max.Ui;

/// <summary>Welche Schritte beim Start laufen.</summary>
internal static class StartupPlan
{
    public static IReadOnlyList<StartupStep> Normal(Action<SystemSnapshot> onHardware) =>
    [
        Hardware(onHardware),
        // TODO (Schritt 19/20): echter Update-Check über das Manifest.
        new("Suche nach Updates", async (_, ct) => { await Task.Delay(700, ct); return "aktuell"; }),
        // TODO (Schritt 10): Modell laden.
        new("Lade Max", async (_, ct) => { await Task.Delay(1100, ct); return "bereit"; }),
    ];

    /// <summary>
    /// Der erste Start: Hardware prüfen, passende Stufe wählen, Modell laden und einrichten.
    /// Die Stufe bleibt unsichtbar – der Nutzer sieht nur "Download Max".
    /// </summary>
    public static IReadOnlyList<StartupStep> Setup(MaxPaths paths, HttpClient http, Action<SystemSnapshot> onHardware)
    {
        var tier = Tier.S;
        TierEntry? entry = null;
        DownloadResult? download = null;

        return
        [
            Hardware(system =>
            {
                tier = TierSelector.Resolve(system.Hardware);
                onHardware(system);
            }),
            new("Download Max", async (progress, ct) =>
            {
                var manifest = await ManifestSource.LoadAsync(http, ct);
                entry = manifest.For(tier) ?? throw new SetupException("Für diesen Rechner ist gerade kein Download hinterlegt.");
                download = await new ModelDownloader(http).DownloadAsync(entry, paths, progress, ct);
                return $"{Format.Gigabytes(download.SizeBytes)} GB";
            }),
            new("Richte Max ein", (_, _) =>
            {
                InstallState.Commit(paths, tier, entry!.Revision, download!, DateTime.Now);
                return Task.FromResult("fertig");
            }),
        ];
    }

    /// <summary>
    /// Vorschau des ersten Starts mit simuliertem Download (<c>max --demo-first-start</c>) –
    /// lädt nichts und schreibt nichts.
    /// </summary>
    public static IReadOnlyList<StartupStep> FirstStartDemo(Action<SystemSnapshot> onHardware) =>
    [
        Hardware(onHardware),
        new("Download Max", SimulateDownloadAsync),
        new("Richte Max ein", async (_, ct) => { await Task.Delay(1000, ct); return "fertig"; }),
    ];

    private static StartupStep Hardware(Action<SystemSnapshot> onHardware) =>
        new("Analysiere Hardware", async (_, ct) =>
        {
            // Eigener Thread: nvidia-smi kann einen Moment brauchen, der Spinner soll weiterlaufen.
            var system = await Task.Run(SystemSnapshot.Capture, ct);
            onHardware(system);
            return system.Summary;
        });

    private static async Task<string> SimulateDownloadAsync(StepProgress progress, CancellationToken ct)
    {
        const long total = 5_100_000_000;
        var duration = TimeSpan.FromSeconds(4);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (clock.Elapsed < duration)
        {
            var fraction = clock.Elapsed / duration;
            var speed = 38_000_000 + 6_000_000 * Math.Sin(clock.Elapsed.TotalMilliseconds / 300);
            progress.Report((long)(fraction * total), total, speed);
            await Task.Delay(50, ct);
        }

        progress.Report(total, total, 0);
        return "5,1 GB";
    }
}
