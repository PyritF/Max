using Max.Chat;
using Max.Commands;
using Max.Ui;
using Spectre.Console;

namespace Max;

/// <summary>
/// Die Chat-Schleife: Eingabe lesen → Befehl ausführen oder Max antworten lassen → wiederholen.
/// Kümmert sich außerdem um Strg+C.
/// </summary>
internal sealed class ChatLoop
{
    private readonly IAnsiConsole _console;
    private readonly IChatBackend _backend;
    private readonly Func<DateTime> _clock;
    private readonly Conversation _conversation = new();
    private readonly CommandRegistry _commands;
    private readonly CtrlCPolicy _ctrlC = new();
    private readonly ChatView _view;
    private readonly InputBox _input;
    private readonly ChoiceMenu? _menu;

    // Gesetzt, solange Max antwortet – Strg+C bricht dann nur die Antwort ab.
    private CancellationTokenSource? _reply;

    // Gesetzt, solange das Auswahlmenü offen ist – Strg+C wird dann ignoriert (Esc schließt es).
    private volatile bool _menuOpen;

    /// <param name="historyFile">Wo frühere Eingaben gespeichert werden (↑/↓); null = nur für diese Sitzung.</param>
    public ChatLoop(IAnsiConsole console, IChatBackend backend, Func<DateTime> clock, CommandRegistry? commands = null, string? historyFile = null)
    {
        _commands = commands ?? CommandRegistry.CreateDefault();
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        _console = console;
        _backend = backend;
        _clock = clock;
        _view = new ChatView(console, animate: interactive);
        var editor = new LineEditor(new InputHistory(historyFile), () => _commands.Visible.Select(c => c.Name));
        _input = new InputBox(console, clock, fancy: interactive, editor);
        _menu = interactive ? new ChoiceMenu(console, clock) : null;
    }

    public async Task RunAsync()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            while (true)
            {
                // Hatte Max eine Rückfrage mit Antworten, erst das Auswahlmenü – Esc führt zur normalen Eingabe.
                var line = AskPendingQuestion();
                if (line is not null)
                {
                    _view.WriteUserMessage(line);
                    await ReplyAsync(line);
                    continue;
                }

                line = _input.ReadLine();

                if (line is null)
                {
                    // Unter Windows beendet Strg+C das Einlesen mit null – das ist kein Eingabe-Ende.
                    if (_ctrlC.WasPressedRecently(_clock()))
                    {
                        _input.Erase();
                        continue;
                    }

                    _input.MoveBelow();
                    ExitCommand.WriteFarewell(_console);
                    return;
                }

                _input.Erase();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                _view.WriteUserMessage(line);

                if (CommandRegistry.IsCommand(line))
                {
                    if (await RunCommandAsync(line) == CommandResult.Exit)
                        return;
                    continue;
                }

                await ReplyAsync(line);
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    private string? AskPendingQuestion()
    {
        if (_menu is null || _view.LastQuestion is not { } question)
            return null;
        _menuOpen = true;
        try
        {
            return _menu.Ask(question);
        }
        finally
        {
            _menuOpen = false;
            _view.ClearQuestion();
        }
    }

    private async Task<CommandResult> RunCommandAsync(string line)
    {
        var (name, args) = CommandRegistry.Parse(line);
        var command = _commands.Find(name);

        if (command is null)
        {
            _view.WriteMaxLine(
                $"[{Theme.Tag(Theme.Text)}]Unbekannter Befehl[/] [{Theme.Tag(Theme.Accent)}]/{Markup.Escape(name)}[/][{Theme.Tag(Theme.Text)}]. [/]" +
                $"[{Theme.Tag(Theme.Accent)}]/help[/] [{Theme.Tag(Theme.Text)}]weiß mehr.[/]");
            return CommandResult.Continue;
        }

        return await command.ExecuteAsync(new CommandContext(_console, _conversation, _commands, _menu is null ? null : _view.SetQuestion), args);
    }

    private async Task ReplyAsync(string line)
    {
        _conversation.AddUser(line);

        using var reply = new CancellationTokenSource();
        _reply = reply;
        try
        {
            var text = await _view.StreamReplyAsync(_backend.StreamReplyAsync(_conversation, reply.Token), reply.Token);
            if (text.Length > 0)
                _conversation.AddAssistant(text);
        }
        finally
        {
            _reply = null;
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Max nicht hart beenden lassen – wir entscheiden selbst.
        e.Cancel = true;

        if (_reply is { } reply)
        {
            reply.Cancel();
            return;
        }

        if (_menuOpen)
            return;

        if (_ctrlC.Press(_clock()) == CtrlCPolicy.Action.Exit)
        {
            _input.MoveBelow();
            ExitCommand.WriteFarewell(_console);
            Environment.Exit(0);
        }

        _input.ShowHint("Nochmal Strg+C zum Beenden");
    }
}
