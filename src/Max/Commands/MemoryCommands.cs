using System.Globalization;
using Max.Memory;
using Max.Ui;
using Spectre.Console;

namespace Max.Commands;

/// <summary>/gedächtnis – zeigt, was Max sich über den Nutzer gemerkt hat.</summary>
internal sealed class MemoryCommand(MemoryBook memory) : ICommand
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public string Name => "gedächtnis";
    public IReadOnlyList<string> Aliases => ["gedaechtnis", "memory"];
    public string Description => "Zeigt, was ich mir über dich gemerkt habe.";
    public bool Hidden => false;

    public Task<CommandResult> ExecuteAsync(CommandContext context, string args)
    {
        var accent = Theme.Tag(Theme.Accent);
        var muted = Theme.Tag(Theme.Muted);
        var text = Theme.Tag(Theme.Text);
        var current = memory.Current;

        if (current.Facts.Count == 0)
        {
            context.Console.MarkupLine($" [{accent}]{Theme.Symbol}[/] [{text}]Noch nichts. Ich merke mir das Wichtigste, wenn wir ein Gespräch beenden.[/]");
            context.Console.WriteLine();
            return Task.FromResult(CommandResult.Continue);
        }

        context.Console.MarkupLine($" [{accent}]{Theme.Symbol}[/] [{text}]Was ich über dich weiß – alles nur hier auf dem Rechner:[/]");
        context.Console.WriteLine();
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadLeft(3).PadRight(2).RightAligned())
            .AddColumn(new GridColumn())
            .AddColumn(new GridColumn().NoWrap().PadLeft(2));
        for (var i = 0; i < current.Facts.Count; i++)
        {
            var fact = current.Facts[i];
            grid.AddRow(
                new Markup($"[{muted}]{i + 1}[/]"),
                new Markup($"[{text}]{Markup.Escape(fact.Text)}[/]"),
                new Markup($"[{muted}]{fact.Added.ToString("d. MMM yyyy", German)}[/]"));
        }
        context.Console.Write(grid);
        context.Console.WriteLine();
        context.Console.MarkupLine($"   [{muted}]Vergessen:[/] [{accent}]/vergiss 3[/] [{muted}]oder[/] [{accent}]/vergiss alles[/]");
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }
}

/// <summary>/vergiss &lt;nr&gt; | alles – löscht Erinnerungen, sofort und auch für das laufende Gespräch.</summary>
internal sealed class ForgetCommand(MemoryBook memory) : ICommand
{
    public string Name => "vergiss";
    public IReadOnlyList<string> Aliases => ["forget"];
    public string Description => "Löscht eine Erinnerung (/vergiss 3) oder alle (/vergiss alles).";
    public bool Hidden => false;

    public Task<CommandResult> ExecuteAsync(CommandContext context, string args)
    {
        var accent = Theme.Tag(Theme.Accent);
        var text = Theme.Tag(Theme.Text);
        var value = args.Trim().ToLowerInvariant();

        string reply;
        if (value is "alles" or "all")
        {
            memory.Set(MemoryData.Empty);
            reply = "Erledigt. Ich weiß nichts mehr über dich. Wir fangen von vorn an.";
        }
        else if (int.TryParse(value, out var number) && memory.Current.Facts.ElementAtOrDefault(number - 1) is { } fact
                 && memory.Current.Without(number) is { } rest)
        {
            memory.Set(rest);
            reply = $"Vergessen: „{fact.Text}“";
        }
        else
        {
            reply = memory.Current.Facts.Count == 0
                ? "Da gibt es nichts zu vergessen."
                : $"Welche Nummer? [/][{accent}]/gedächtnis[/][{text}] zeigt die Liste.";
        }

        context.Console.MarkupLine($" [{accent}]{Theme.Symbol}[/] [{text}]{(reply.Contains("[/]") ? reply : Markup.Escape(reply))}[/]");
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }
}
