using Max.Llm;
using Max.Persona;
using Max.Setup;
using Max.Update;

namespace Max.Ui;

/// <summary>Welche Schritte beim Start laufen.</summary>
internal static class StartupPlan
{
    /// <param name="onManifest">Bekommt das Manifest von GitHub (null = offline) – der Updater arbeitet damit weiter.</param>
    public static IReadOnlyList<StartupStep> Normal(
        MaxPaths paths, HttpClient http, Updater updater, Func<bool> thinking,
        Action<SystemSnapshot> onHardware, Action<Manifest?> onManifest, Action<LlmEngine, LlmBackend> onLoaded, Tools.ToolBox? tools = null)
    {
        SystemSnapshot? system = null;
        return
        [
            Hardware(s => { system = s; onHardware(s); }, checkRequirements: true),
            new("Suche nach Updates", async (progress, ct) =>
            {
                var manifest = await ManifestSource.TryLoadRemoteAsync(http, ct);
                onManifest(manifest);
                return manifest is null
                    ? "offline"
                    : await StartupUpdate.CheckAsync(manifest, updater, AppVersion.Current, AppVersion.ExecutablePath, progress, ct);
            }),
            Load(paths, () => system, thinking, onLoaded, tools),
        ];
    }

    /// <summary>
    /// Der erste Start: Hardware prüfen (vor dem Download – sonst lädt ein zu schwacher Rechner umsonst
    /// mehrere GB), Modell laden und einrichten.
    /// </summary>
    public static IReadOnlyList<StartupStep> Setup(MaxPaths paths, HttpClient http, Func<bool> thinking, Action<SystemSnapshot> onHardware, Action<LlmEngine, LlmBackend> onLoaded, Tools.ToolBox? tools = null)
    {
        SystemSnapshot? system = null;
        ModelEntry? entry = null;
        DownloadResult? download = null;

        return
        [
            Hardware(s => { system = s; onHardware(s); }, checkRequirements: true),
            new("Download Max", async (progress, ct) =>
            {
                var manifest = await ManifestSource.LoadAsync(http, ct);
                entry = manifest.Model!;
                download = await new ModelDownloader(http).DownloadAsync(entry, paths, progress, ct);
                return $"{Format.Gigabytes(download.SizeBytes)} GB";
            }),
            new("Richte Max ein", (_, _) =>
            {
                InstallState.Commit(paths, entry!, download!, DateTime.Now);
                return Task.FromResult("fertig");
            }),
            Load(paths, () => system, thinking, onLoaded, tools),
        ];
    }

    /// <summary>
    /// Lädt das Modell (mit Balken) und wärmt es auf: System-Prompt vorrechnen, Grafikkarte einrichten.
    /// Danach kommt die erste Antwort ohne Wartezeit.
    /// </summary>
    private static StartupStep Load(MaxPaths paths, Func<SystemSnapshot?> system, Func<bool> thinking, Action<LlmEngine, LlmBackend> onLoaded, Tools.ToolBox? tools) =>
        new("Lade Max", async (progress, ct) =>
        {
            var state = InstallState.Load(paths) ?? throw new SetupException("Max ist nicht vollständig eingerichtet. Starte ihn einfach neu.");
            var snapshot = system() ?? SystemSnapshot.Capture();
            LlmEngine? engine = null;
            try
            {
                engine = await LlmEngine.LoadAsync(paths.Model, state.ContextSize, snapshot.Hardware, paths.EngineLog, progress, ct);
                var prompt = SystemPrompt.BuildParts(snapshot, Memory.MemoryStore.Load(paths), tools);
                var options = BackendOptionsFor(state, thinking) with
                {
                    Tools = tools,
                    PromptCache = new PromptCache(paths.PromptCache, $"{state.Sha256}|{engine.StateIdentity}"),
                    StablePromptLength = prompt.StableLength,
                };
                var backend = new LlmBackend(engine, prompt.Text, options);
                await backend.WarmUpAsync(ct);
                onLoaded(engine, backend);
                return "bereit";
            }
            catch (OperationCanceledException)
            {
                engine?.Dispose();
                throw;
            }
            catch (Exception e)
            {
                engine?.Dispose();
                LlmEngine.Log($"Laden fehlgeschlagen: {e}");
                throw new SetupException($"Max ließ sich nicht laden. Näheres steht in {paths.EngineLog}.", e);
            }
        });

    /// <summary>Denk-Budget aus dem Manifest; <c>MAX_GRAMMAR=0</c> schaltet die Grammatik ab (Fehlersuche).</summary>
    private static BackendOptions BackendOptionsFor(InstallState state, Func<bool> thinking)
    {
        return new BackendOptions(
            ThinkingBudget: ManifestSource.Embedded().Model!.ThinkingBudget,
            ThinkingEnabled: thinking,
            UseGrammar: Environment.GetEnvironmentVariable("MAX_GRAMMAR") != "0");
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

    private static StartupStep Hardware(Action<SystemSnapshot> onHardware, bool checkRequirements = false) =>
        new("Analysiere Hardware", async (_, ct) =>
        {
            // Eigener Thread: nvidia-smi kann einen Moment brauchen, der Spinner soll weiterlaufen.
            var system = await Task.Run(SystemSnapshot.Capture, ct);
            onHardware(system);
            if (checkRequirements && Requirements.Problem(system.Hardware, Requirements.CpuAllowed) is { } problem)
                throw new SetupException(problem);
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
