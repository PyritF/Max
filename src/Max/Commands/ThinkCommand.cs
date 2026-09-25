using Max.Setup;
using Max.Ui;
using Spectre.Console;

namespace Max.Commands;

/// <summary>Ob Max vor dem Antworten nachdenkt – gemerkt in settings.json.</summary>
internal sealed class ThinkingSwitch(MaxPaths? paths, bool enabled)
{
    public bool Enabled { get; private set; } = enabled;

    public static ThinkingSwitch Load(MaxPaths paths) => new(paths, Settings.Load(paths).Thinking);

    public void Set(bool enabled)
    {
        Enabled = enabled;
        if (paths is not null)
            (Settings.Load(paths) with { Thinking = enabled }).Save(paths);
    }
}

/// <summary>/denken an|aus – ohne Angabe wird umgeschaltet.</summary>
internal sealed class ThinkCommand(ThinkingSwitch thinking) : ICommand
{
    public string Name => "denken";
    public IReadOnlyList<string> Aliases => ["think"];
    public string Description => "Schaltet das Nachdenken vor jeder Antwort an oder aus.";
    public bool Hidden => false;

    public Task<CommandResult> ExecuteAsync(CommandContext context, string args)
    {
        var value = args.Trim().ToLowerInvariant();
        bool? wanted = value switch
        {
            "an" or "ein" or "on" => true,
            "aus" or "off" => false,
            "" => !thinking.Enabled,
            _ => null,
        };

        var text = Theme.Tag(Theme.Text);
        var accent = Theme.Tag(Theme.Accent);
        if (wanted is null)
        {
            context.Console.MarkupLine($" [{accent}]{Theme.Symbol}[/] [{text}]Bitte[/] [{accent}]/denken an[/] [{text}]oder[/] [{accent}]/denken aus[/][{text}].[/]");
        }
        else
        {
            thinking.Set(wanted.Value);
            context.Console.MarkupLine(wanted.Value
                ? $" [{accent}]{Theme.Symbol}[/] [{text}]Ich denke wieder nach, bevor ich antworte.[/]"
                : $" [{accent}]{Theme.Symbol}[/] [{text}]Ab jetzt antworte ich direkt. Schneller, nicht unbedingt klüger.[/]");
        }
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }
}
