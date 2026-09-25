using Max.Ui;
using Spectre.Console;

namespace Max.Commands;

internal sealed class ClearCommand : ICommand
{
    public string Name => "clear";
    public IReadOnlyList<string> Aliases => [];
    public string Description => "Vergisst das bisherige Gespräch und leert den Bildschirm.";
    public bool Hidden => false;

    public Task<CommandResult> ExecuteAsync(CommandContext context, string args)
    {
        context.Conversation.Clear();
        context.Console.Clear();
        context.Console.WriteLine();
        context.Console.MarkupLine($" [{Theme.Tag(Theme.Accent)}]{Theme.Symbol}[/] [{Theme.Tag(Theme.Text)}]Alles vergessen. Fast bedauerlich.[/]");
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }
}
