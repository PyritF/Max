using Max;
using Max.Chat;
using Max.Ui;
using Spectre.Console;

// Damit ◆, Rahmen und Umlaute auch in der Windows-Konsole korrekt erscheinen.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// Bei umgeleiteter Ausgabe (Datei, Pipe) kennt Spectre keine Fensterbreite (-1) – dann feste Breite.
if (AnsiConsole.Profile.Width <= 0)
    AnsiConsole.Profile.Width = 100;

SystemSnapshot? system = null;
void OnHardware(SystemSnapshot s) => system = s;

// 1. Startsequenz (Hardware, Updates, Laden – bzw. beim ersten Start: Download)
if (args.Contains("--demo-first-start"))
    await StartupScreen.RunAsync("Einrichtung", "Einmalig. Danach geht es deutlich schneller.", StartupPlan.FirstStartDemo(OnHardware));
else
    await StartupScreen.RunAsync("Max startet", null, StartupPlan.Normal(OnHardware));

// 2. Übersicht
if (!Console.IsOutputRedirected)
    AnsiConsole.Clear();

await HomeScreen.ShowAsync(system ?? SystemSnapshot.Capture());

// 3. Chat
await new ChatLoop(AnsiConsole.Console, new PlaceholderBackend(), () => DateTime.Now).RunAsync();
