using Max;
using Max.Chat;
using Max.Setup;
using Max.Ui;
using Spectre.Console;

// Damit ◆, Rahmen und Umlaute auch in der Windows-Konsole korrekt erscheinen.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Bei umgeleiteter Ausgabe (Datei, Pipe) kennt Spectre keine Fensterbreite (-1) – dann feste Breite.
if (AnsiConsole.Profile.Width <= 0)
    AnsiConsole.Profile.Width = 100;

var paths = MaxPaths.Default();

// Weiterleitungen folgt der Downloader selbst (siehe ModelDownloader); Zeitlimits setzt jede Anfrage selbst.
using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.UserAgent.ParseAdd($"Max/{typeof(Program).Assembly.GetName().Version?.ToString(3)}");

SystemSnapshot? system = null;
void OnHardware(SystemSnapshot s) => system = s;

// 1. Startsequenz – beim ersten Start die Einrichtung mit Download.
//    Strg+C bricht hier sauber ab; ein angefangener Download bleibt liegen und geht beim nächsten Mal weiter.
using (var startup = new CancellationTokenSource())
{
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; startup.Cancel(); };
    Console.CancelKeyPress += onCancel;
    try
    {
        if (args.Contains("--demo-first-start"))
            await StartupScreen.RunAsync("Einrichtung", "Einmalig. Danach geht es deutlich schneller.", StartupPlan.FirstStartDemo(OnHardware), startup.Token);
        else if (!InstallState.IsInstalled(paths))
            await StartupScreen.RunAsync("Einrichtung", "Einmalig. Danach geht es deutlich schneller.", StartupPlan.Setup(paths, http, OnHardware), startup.Token);
        else
            await StartupScreen.RunAsync("Max startet", null, StartupPlan.Normal(OnHardware), startup.Token);
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

// 2. Übersicht
if (!Console.IsOutputRedirected)
    AnsiConsole.Clear();

await HomeScreen.ShowAsync(system ?? SystemSnapshot.Capture());

// 3. Chat
await new ChatLoop(AnsiConsole.Console, new PlaceholderBackend(), () => DateTime.Now).RunAsync();
return 0;
