using Max.Ui;
using Spectre.Console;

// Damit ◆, Rahmen und Umlaute auch in der Windows-Konsole korrekt erscheinen.
Console.OutputEncoding = System.Text.Encoding.UTF8;

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
