namespace Max.Ui;

/// <summary>
/// Welche Schritte beim Start laufen. Solange die echten Bausteine (Update-Check, Download,
/// Modell laden) noch fehlen, stehen hier Platzhalter mit kurzer Wartezeit.
/// </summary>
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
    /// Vorschau des ersten Starts mit simuliertem Download (<c>max --demo-first-start</c>).
    /// Wird ersetzt, sobald es den echten Download gibt (Schritt 8).
    /// </summary>
    public static IReadOnlyList<StartupStep> FirstStartDemo(Action<SystemSnapshot> onHardware) =>
    [
        Hardware(onHardware),
        new("Download Max", SimulateDownloadAsync),
        new("Richte Max ein", async (_, ct) => { await Task.Delay(1000, ct); return "fertig"; }),
    ];

    private static StartupStep Hardware(Action<SystemSnapshot> onHardware) =>
        new("Analysiere Hardware", (_, _) =>
        {
            var system = SystemSnapshot.Capture();
            onHardware(system);
            return Task.FromResult(system.Summary);
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
