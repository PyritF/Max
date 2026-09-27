using Max;
using Max.Chat;
using Max.Commands;
using Max.Llm;
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
            await StartupScreen.RunAsync("Einrichtung", setupSubtitle, StartupPlan.Setup(paths, http, () => thinking.Enabled, OnHardware, OnLoaded), startup.Token);
        else
            await StartupScreen.RunAsync("Max startet", null, StartupPlan.Normal(paths, http, updater, () => thinking.Enabled, OnHardware, m => manifest = m, OnLoaded), startup.Token);
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

// Nur für den GitHub-Workflow: feste Fragen statt Chat.
if (args.Contains("--selftest") && engine is not null && llm is not null)
    return await SelfTest.RunAsync(engine, llm, Console.Out);

// 2. Übersicht
if (!Console.IsOutputRedirected)
    AnsiConsole.Clear();

await HomeScreen.ShowAsync(system);

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
var commands = CommandRegistry.CreateDefault(new ThinkCommand(thinking), new DebugCommand(() => DebugReport.Build(engine, llm, paths, system, updater)), new DemoCommand());
await new ChatLoop(AnsiConsole.Console, backend, () => DateTime.Now, commands, paths.History).RunAsync();
if (llm is not null)
    await llm.CompleteAsync(); // erst fertig nachrechnen, dann das Modell freigeben
updates.Cancel();
await updating;
return 0;
