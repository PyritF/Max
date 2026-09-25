using Max.Ui;
using Max.Ui.Markdown;
using Max.Ui.Widgets;
using Spectre.Console;

namespace Max.Commands;

/// <summary>
/// Zeigt alles, was Max im Terminal darstellen kann – ohne Modell. Versteckt; nützlich, um die
/// Darstellung im eigenen Terminal zu prüfen. <c>/demo schriften</c> zeigt alle Schriftarten.
/// </summary>
internal sealed class DemoCommand : ICommand
{
    public string Name => "demo";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Zeigt alle Darstellungsmöglichkeiten.";
    public bool Hidden => true;

    internal const string Showcase = """
        ## Markdown
        Text mit **fett**, *kursiv*, `code`, {cyan}Farbe{/cyan}, {verlauf}einem Farbverlauf{/verlauf} ({verlauf:grün-blau}auch in eigenen Farben{/verlauf}) und Emoji :rocket:.

        - Listen mit Einzug
          - auch verschachtelt
        1. nummeriert

        > Ein Zitat.

        --- Linie mit Titel ---

        ```csharp
        // Syntax-Hervorhebung passiert automatisch
        public static int Add(int a, int b) => a + b; /* Kommentar */
        ```

        ```balken
        Titel: Beliebte Sprachen
        C#: 42
        Python: 38
        JavaScript: 30
        Rust: 12
        ```

        ```anteile
        Titel: Speicher
        Modell: 5,7
        System: 3,1
        frei: 7,2
        ```

        ```kurve
        Titel: Temperatur
        Verlauf: grün-blau
        Mo: 12
        Di: 15
        Mi: 11
        Do: 18
        Fr: 21
        Sa: 19
        So: 23
        ```

        ```fortschritt
        Download: 70
        Tests: 100
        Doku: 35
        ```

        ```baum
        Max/
          src/
            Program.cs
            Ui/
          tests/
          PLAN.md
        ```

        ```kasten
        Titel: Hinweis
        Farbe: gelb
        Das ist ein **Kasten** für Hinweise.
        - mit Aufzählung
        ```

        ```spalten
        **Vorteile**
        - lokal
        - schnell
        ---
        **Nachteile**
        - braucht Speicher
        ```

        ```kalender
        Monat: 2026-10
        Markiert: 3, 17, 31
        ```

        ```titel
        Text: Max
        Schrift: slant
        Verlauf: rot-gelb
        ```
        """;

    public Task<CommandResult> ExecuteAsync(CommandContext context, string args)
    {
        var text = args.Trim().Equals("schriften", StringComparison.OrdinalIgnoreCase)
            ? string.Join("\n\n", TitleWidget.BuiltInFonts.Select(f => $"--- {f} ---\n```titel\nText: Max\nSchrift: {f}\nVerlauf: blau-cyan\n```"))
            : Showcase;

        context.Console.Markup($" [{Theme.Tag(Theme.Accent)}]{Theme.Symbol}[/] ");
        var writer = new WrapWriter(context.Console, 3);
        var renderer = new MarkdownRenderer(context.Console, writer);
        renderer.Push(text);
        renderer.Finish();
        writer.CloseReply();
        return Task.FromResult(CommandResult.Continue);
    }
}
