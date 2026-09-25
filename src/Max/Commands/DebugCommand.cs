using Max.Ui;
using Spectre.Console;

namespace Max.Commands;

/// <summary>
/// Technische Innereien: Modell, Stufe, Backend, Geschwindigkeit. Versteckt – taucht in /help nicht auf.
/// </summary>
internal sealed class DebugCommand(Func<IReadOnlyList<(string Label, string Value)>> rows) : ICommand
{
    public string Name => "debug";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Technische Details.";
    public bool Hidden => true;

    public Task<CommandResult> ExecuteAsync(CommandContext context, string args)
    {
        var muted = Theme.Tag(Theme.Muted);
        var text = Theme.Tag(Theme.Text);

        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadLeft(3).PadRight(3))
            .AddColumn(new GridColumn());
        foreach (var (label, value) in rows())
            grid.AddRow(new Markup($"[{muted}]{Markup.Escape(label)}[/]"), new Markup($"[{text}]{Markup.Escape(value)}[/]"));

        context.Console.MarkupLine($" [{Theme.Tag(Theme.Accent)}]{Theme.Symbol}[/] [{text}]Unter der Haube. Nicht weitersagen.[/]");
        context.Console.WriteLine();
        context.Console.Write(grid);
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }
}
