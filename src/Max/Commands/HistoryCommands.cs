using Max.Chat;
using Max.Ui;
using Spectre.Console;

namespace Max.Commands;

/// <summary>/verlauf – unsere letzten Gespräche; /verlauf löschen räumt sie weg.</summary>
internal sealed class HistoryCommand(ConversationArchive archive, Func<DateTime> clock) : ICommand
{
    internal const int Shown = 10;

    public string Name => "verlauf";
    public IReadOnlyList<string> Aliases => ["gespräche", "gespraeche"];
    public string Description => "Zeigt unsere letzten Gespräche (/verlauf löschen löscht sie).";
    public bool Hidden => false;

    public Task<CommandResult> ExecuteAsync(CommandContext context, string args)
    {
        var accent = Theme.Tag(Theme.Accent);
        var muted = Theme.Tag(Theme.Muted);
        var text = Theme.Tag(Theme.Text);

        if (args.Trim().ToLowerInvariant() is "löschen" or "loeschen" or "leeren")
        {
            var count = archive.DeleteAll();
            context.Conversation.Id = null;         // das laufende wird beim nächsten Mal neu gespeichert
            var reply = count == 0 ? "Da war nichts gespeichert." : $"Gelöscht – {count} {(count == 1 ? "Gespräch" : "Gespräche")}. Mein Gedächtnis bleibt (/gedächtnis).";
            context.Console.MarkupLine($" [{accent}]{Theme.Symbol}[/] [{text}]{Markup.Escape(reply)}[/]");
            context.Console.WriteLine();
            return Task.FromResult(CommandResult.Continue);
        }

        var earlier = ContinueCommand.Earlier(archive, context.Conversation);
        if (earlier.Count == 0)
        {
            context.Console.MarkupLine($" [{accent}]{Theme.Symbol}[/] [{text}]Noch keine früheren Gespräche. Ich hebe sie auf, sobald wir uns unterhalten.[/]");
            context.Console.WriteLine();
            return Task.FromResult(CommandResult.Continue);
        }

        context.Console.MarkupLine($" [{accent}]{Theme.Symbol}[/] [{text}]Unsere letzten Gespräche – alles nur hier auf dem Rechner:[/]");
        context.Console.WriteLine();
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadLeft(3).PadRight(2).RightAligned())
            .AddColumn(new GridColumn().NoWrap().PadRight(2))
            .AddColumn(new GridColumn())
            .AddColumn(new GridColumn().NoWrap().PadLeft(2));
        var now = clock();
        for (var i = 0; i < Math.Min(Shown, earlier.Count); i++)
        {
            var saved = earlier[i];
            grid.AddRow(
                new Markup($"[{muted}]{i + 1}[/]"),
                new Markup($"[{muted}]{Markup.Escape(ConversationArchive.When(saved.Updated, now))}[/]"),
                new Markup($"[{text}]{Markup.Escape(saved.Title)}[/]"),
                new Markup($"[{muted}]{saved.Count} Nachrichten[/]"));
        }
        context.Console.Write(grid);
        context.Console.WriteLine();
        context.Console.MarkupLine($"   [{muted}]Weitermachen:[/] [{accent}]/weiter[/] [{muted}]oder[/] [{accent}]/weiter 2[/][{muted}] · Löschen:[/] [{accent}]/verlauf löschen[/]");
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }
}

/// <summary>/weiter [Nr] – setzt ein früheres Gespräch fort; ohne Nummer das letzte.</summary>
internal sealed class ContinueCommand(ConversationArchive archive, Func<DateTime> clock) : ICommand
{
    public string Name => "weiter";
    public IReadOnlyList<string> Aliases => ["fortsetzen"];
    public string Description => "Macht im letzten Gespräch weiter (/weiter 2 im vorletzten – siehe /verlauf).";
    public bool Hidden => false;

    /// <summary>Die gespeicherten Gespräche ohne das laufende, das zuletzt geführte zuerst.</summary>
    internal static List<SavedConversation> Earlier(ConversationArchive archive, Conversation current) =>
        archive.List().Where(c => c.Id != current.Id).ToList();

    public Task<CommandResult> ExecuteAsync(CommandContext context, string args)
    {
        var accent = Theme.Tag(Theme.Accent);
        var muted = Theme.Tag(Theme.Muted);
        var text = Theme.Tag(Theme.Text);
        var earlier = Earlier(archive, context.Conversation);
        var number = args.Trim().Length == 0 ? 1 : int.TryParse(args.Trim(), out var n) ? n : 0;

        if (earlier.Count == 0 || number < 1 || number > earlier.Count || !archive.Load(earlier[number - 1].Id, context.Conversation))
        {
            var reply = earlier.Count == 0 ? "Es gibt noch kein früheres Gespräch." : $"Welches? [/][{accent}]/verlauf[/][{text}] zeigt die Liste.";
            context.Console.MarkupLine($" [{accent}]{Theme.Symbol}[/] [{text}]{reply}[/]");
            context.Console.WriteLine();
            return Task.FromResult(CommandResult.Continue);
        }

        var saved = earlier[number - 1];
        context.Console.MarkupLine($" [{accent}]{Theme.Symbol}[/] [{text}]Weiter im Gespräch von {Markup.Escape(ConversationArchive.When(saved.Updated, clock()))} – „{Markup.Escape(saved.Title)}“. Zuletzt:[/]");
        var messages = context.Conversation.Messages;
        var question = messages.LastOrDefault(m => m.Role == ChatRole.User);
        var answer = messages.LastOrDefault(m => m.Role == ChatRole.Assistant && !m.Content.StartsWith("```werkzeug", StringComparison.Ordinal));
        if (question is not null)
            context.Console.MarkupLine($"   [{muted}]›[/] [{Theme.Tag(Theme.Dim)}]{Markup.Escape(Preview(question.Content, 160))}[/]");
        if (answer is not null && (question is null || answer.Timestamp >= question.Timestamp))
            context.Console.MarkupLine($"   [{muted}]{Theme.Symbol} {Markup.Escape(Preview(ColorTags.Strip(answer.Content), 240))}[/]");
        context.Console.WriteLine();
        return Task.FromResult(CommandResult.Continue);
    }

    private static string Preview(string text, int max)
    {
        var line = string.Join(' ', text.ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }
}
