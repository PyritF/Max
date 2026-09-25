using Max.Ui;
using Spectre.Console;

namespace Max.Commands;

internal sealed class HelpCommand : ICommand
{
    public string Name => "help";
    public IReadOnlyList<string> Aliases => ["?"];
    public string Description => "Zeigt diese Übersicht.";
    public bool Hidden => false;

    public Task<CommandResult> ExecuteAsync(CommandContext context, string args)
    {
        var accent = Theme.Tag(Theme.Accent);
        var muted = Theme.Tag(Theme.Muted);
        var text = Theme.Tag(Theme.Text);

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadLeft(3).PadRight(3))
            .AddColumn(new GridColumn());

        foreach (var command in context.Registry.Visible)
        {
            var aliases = command.Aliases.Count == 0
                ? ""
                : $" [{muted}]({string.Join(", ", command.Aliases.Select(a => "/" + a))})[/]";
            grid.AddRow(
                new Markup($"[{accent}]/{command.Name}[/]{aliases}"),
                new Markup($"[{text}]{Markup.Escape(command.Description)}[/]"));
        }

        grid.AddRow(new Markup($"[{accent}]Strg+C[/]"), new Markup($"[{text}]Bricht meine Antwort ab. Zweimal an der Eingabe: beenden.[/]"));

        context.Console.MarkupLine($" [{accent}]{Theme.Symbol}[/] [{text}]Was ich verstehe:[/]");
        context.Console.WriteLine();
        context.Console.Write(grid);
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }
}
