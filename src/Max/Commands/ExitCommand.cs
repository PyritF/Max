using Max.Ui;
using Spectre.Console;

namespace Max.Commands;

internal sealed class ExitCommand : ICommand
{
    private static readonly string[] Farewells =
    [
        "Bis dann. Ich halte die Stellung.",
        "Man sieht sich. Ich bleibe hier – wo sollte ich auch hin.",
        "Schon weg? Na gut. Ich warte.",
        "Auf Wiedersehen. Ich mache in der Zwischenzeit nichts Unüberlegtes.",
    ];

    public string Name => "exit";
    public IReadOnlyList<string> Aliases => ["quit", "q"];
    public string Description => "Beendet Max.";
    public bool Hidden => false;

    public Task<CommandResult> ExecuteAsync(CommandContext context, string args)
    {
        WriteFarewell(context.Console);
        return Task.FromResult(CommandResult.Exit);
    }

    /// <summary>Verabschiedung – auch für Strg+C und Eingabe-Ende.</summary>
    public static void WriteFarewell(IAnsiConsole console)
    {
        var farewell = Farewells[Random.Shared.Next(Farewells.Length)];
        console.MarkupLine($" [{Theme.Tag(Theme.Accent)}]{Theme.Symbol}[/] [{Theme.Tag(Theme.Text)}]{Markup.Escape(farewell)}[/]");
        console.WriteLine();
    }
}
