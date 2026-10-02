using Max;
using Max.Chat;
using Max.Commands;
using Max.Llm;
using Max.Memory;
using Max.Persona;
using Max.Setup;
using Max.Ui;
using Max.Update;
using Spectre.Console;

// Damit ◆, Rahmen und Umlaute auch in der Windows-Konsole korrekt erscheinen.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Bei umgeleiteter Ausgabe (Datei, Pipe) kennt Spectre keine Fensterbreite (-1) – dann feste Breite.
if (AnsiConsole.Profile.Width <= 0)
    AnsiConsole.Profile.Width = 100;

var paths = MaxPaths.Default();

Max.Ui.Widgets.TitleWidget.UserFontDirectory = Path.Combine(paths.Root, "fonts");
var demo = args.Contains("--demo-first-start");

// Weiterleitungen folgt der Downloader selbst (siehe ModelDownloader); Zeitlimits setzt jede Anfrage selbst.
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.UserAgent.ParseAdd($"Max/{typeof(Program).Assembly.GetName().Version?.ToString(3)}");

var thinking = ThinkingSwitch.Load(paths);

SystemSnapshot? system = null;
LlmEngine? engine = null;
LlmBackend? llm = null;

// Bildverständnis: erst, wenn der Bild-Zusatz da ist (er kommt im Hintergrund) – geladen beim ersten Bild.
VisionEngine? vision = null;
IVision? Vision()
{
    if (vision is not null || engine is null || VisionFile.Installed(paths) is not { } projector)
        return vision;
    // Auf die Grafikkarte nur, wenn neben dem Modell sicher Platz ist; sonst rechnet die CPU (langsamer).
    var roomy = engine.Info.GpuLayers > 0 && system?.Hardware.Gpu is { VramBytes: >= 10L * 1024 * 1024 * 1024 };
    return vision = new VisionEngine(engine, projector, roomy);
}

// Werkzeuge (nur lesend): eigener HttpClient, der Weiterleitungen folgt – Webseiten leiten oft um.
using var web = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 }) { Timeout = Timeout.InfiniteTimeSpan };
var tools = Max.Tools.ToolBox.CreateDefault(web, () => DateTime.Now, () => Environment.CurrentDirectory, Vision);
void OnHardware(SystemSnapshot s) => system = s;
void OnLoaded(LlmEngine e, LlmBackend b) => (engine, llm) = (e, b);
Manifest? manifest = null;

// Bereitliegende Updates aktivieren, bevor irgendetwas geladen wird (PLAN.md §5a).
var exePath = AppVersion.ExecutablePath;
if (!demo)
    UpdateApplier.Apply(paths, exePath);
var updater = new Updater(http, paths, exePath, AppVersion.Current);

// 1. Startsequenz – beim ersten Start die Einrichtung mit Download, dann das Modell laden.
//    Strg+C bricht hier sauber ab; ein angefangener Download bleibt liegen und geht beim nächsten Mal weiter.
using (var startup = new CancellationTokenSource())
{
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; startup.Cancel(); };
    Console.CancelKeyPress += onCancel;
    try
    {
        const string setupSubtitle = "Einmalig. Danach geht es deutlich schneller.";
        if (demo)
            await StartupScreen.RunAsync("Einrichtung", setupSubtitle, StartupPlan.FirstStartDemo(OnHardware), startup.Token);
        else if (!InstallState.IsInstalled(paths))
            await StartupScreen.RunAsync("Einrichtung", setupSubtitle, StartupPlan.Setup(paths, http, () => thinking.Enabled, OnHardware, OnLoaded, tools), startup.Token);
        else
            await StartupScreen.RunAsync("Max startet", null, StartupPlan.Normal(paths, http, updater, () => thinking.Enabled, OnHardware, m => manifest = m, OnLoaded, tools), startup.Token);
    }
    catch (Exception e) when (e is SetupException or OperationCanceledException)
    {
        // Die Meldung steht schon auf dem Bildschirm (StartupScreen).
        AnsiConsole.WriteLine();
        return e is SetupException ? 1 : 130;
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }
}

system ??= SystemSnapshot.Capture();
using var loadedEngine = engine;
using var disposeVision = new Disposer(() => vision?.Dispose());

// Nur für den GitHub-Workflow: feste Fragen statt Chat – vorher den Bild-Zusatz holen, damit auch Bilder drankommen.
if (args.Contains("--selftest") && engine is not null && llm is not null)
{
    var current = manifest ?? await ManifestSource.LoadAsync(http, CancellationToken.None);
    if (VisionFile.IsMissing(paths, current.Vision))
    {
        Console.WriteLine("Lade den Bild-Zusatz …");
        try
        {
            await VisionFile.DownloadAsync(new ModelDownloader(http), paths, current.Vision!, new StepProgress(), CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or SetupException)
        {
            Console.WriteLine($"WARNUNG: Bild-Zusatz nicht geladen ({e.Message}) – Bildfragen gehen dann nicht.");
        }
    }
    return await SelfTest.RunAsync(engine, llm, Console.Out, m => SystemPrompt.Build(system, m, tools));
}

// 2. Übersicht
if (!Console.IsOutputRedirected)
    AnsiConsole.Clear();

// Gedächtnis: Ändert es sich (/vergiss), bekommt das Modell sofort den neuen System-Prompt.
var memory = new MemoryBook(demo ? null : paths, m => llm?.UpdateSystemPrompt(SystemPrompt.Build(system, m, tools)));

await HomeScreen.ShowAsync(system, memory.Current.LastSession);

// Eine Nachricht aus dem Manifest erscheint genau einmal.
if (manifest?.App.Message is { Length: > 0 } message && Settings.Load(paths) is var settings && settings.SeenMessage != message)
{
    AnsiConsole.MarkupLine($"  [{Theme.Accent.ToMarkup()}]◆[/] {Markup.Escape(message)}");
    AnsiConsole.WriteLine();
    (settings with { SeenMessage = message }).Save(paths);
}

// Stille Updates im Hintergrund; beim Schließen pausieren sie und gehen beim nächsten Start weiter.
using var updates = new CancellationTokenSource();
var updating = demo ? Task.CompletedTask : Task.Run(() => updater.RunAsync(manifest, updates.Token));

// 3. Chat – in der Demo ohne Modell mit Platzhalter-Antworten.
IChatBackend backend = llm ?? (IChatBackend)new PlaceholderBackend();
var commands = CommandRegistry.CreateDefault(
    new ThinkCommand(thinking), new MemoryCommand(memory), new ForgetCommand(memory),
    new DebugCommand(() => DebugReport.Build(engine, llm, paths, system, updater)), new DemoCommand());

// Beim Beenden notiert sich Max das Wichtigste – samt Begrüßung für den nächsten Start (PLAN.md §8a).
Func<Conversation, CancellationToken, Task>? remember = llm is null ? null : async (conversation, ct) =>
{
    if (await Reflection.RunAsync(llm, conversation.Messages, DateTime.Now, ct) is { } reflection)
        memory.Set(reflection.ApplyTo(memory.Current, DateTime.Now));
};
var opening = llm is null ? null : memory.TakeGreeting(system.Now);
await new ChatLoop(AnsiConsole.Console, backend, () => DateTime.Now, commands, paths.History, opening, remember).RunAsync();
if (llm is not null)
    await llm.CompleteAsync(); // erst fertig nachrechnen, dann das Modell freigeben
updates.Cancel();
await updating;
return 0;

/// <summary>Räumt am Ende auf (using für etwas, das erst später entsteht).</summary>
internal sealed class Disposer(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}
