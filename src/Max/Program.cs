using Max;
using Max.Chat;
using Max.Commands;
using Max.Llm;
using Max.Persona;
using Max.Setup;
using Max.Ui;
using Spectre.Console;

// Damit ◆, Rahmen und Umlaute auch in der Windows-Konsole korrekt erscheinen.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Bei umgeleiteter Ausgabe (Datei, Pipe) kennt Spectre keine Fensterbreite (-1) – dann feste Breite.
if (AnsiConsole.Profile.Width <= 0)
    AnsiConsole.Profile.Width = 100;

var paths = MaxPaths.Default();
var demo = args.Contains("--demo-first-start");

// Weiterleitungen folgt der Downloader selbst (siehe ModelDownloader); Zeitlimits setzt jede Anfrage selbst.
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.UserAgent.ParseAdd($"Max/{typeof(Program).Assembly.GetName().Version?.ToString(3)}");

SystemSnapshot? system = null;
LlmEngine? engine = null;
void OnHardware(SystemSnapshot s) => system = s;
void OnEngine(LlmEngine e) => engine = e;

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
            await StartupScreen.RunAsync("Einrichtung", setupSubtitle, StartupPlan.Setup(paths, http, OnHardware, OnEngine), startup.Token);
        else
            await StartupScreen.RunAsync("Max startet", null, StartupPlan.Normal(paths, OnHardware, OnEngine), startup.Token);
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
if (args.Contains("--selftest") && engine is not null)
    return await SelfTest.RunAsync(engine, system, Console.Out);

// 2. Übersicht
if (!Console.IsOutputRedirected)
    AnsiConsole.Clear();

await HomeScreen.ShowAsync(system);

// 3. Chat – in der Demo ohne Modell mit Platzhalter-Antworten.
IChatBackend backend = engine is null ? new PlaceholderBackend() : new LlmBackend(engine, SystemPrompt.Build(system));
var commands = CommandRegistry.CreateDefault(new DebugCommand(() => DebugReport.Build(engine, paths, system)));
await new ChatLoop(AnsiConsole.Console, backend, () => DateTime.Now, commands).RunAsync();
return 0;
