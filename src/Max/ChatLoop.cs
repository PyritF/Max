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

    // Was beim Beenden noch passiert (Gedächtnis) – bekommt das Gespräch, Strg+C bricht es ab.
    private readonly Func<Conversation, CancellationToken, Task>? _onExit;

    // Hier landet das Gespräch nach jeder Antwort (für /weiter) – null = nirgends.
    private readonly ConversationArchive? _archive;

    // Gesetzt, solange das Auswahlmenü offen ist – Strg+C wird dann ignoriert (Esc schließt es).
    private volatile bool _menuOpen;

    /// <param name="historyFile">Wo frühere Eingaben gespeichert werden (↑/↓); null = nur für diese Sitzung.</param>
    /// <param name="opening">Max' erster Satz (Begrüßung aus dem Gedächtnis) – steht auch im Verlauf, damit eine Antwort darauf passt.</param>
    /// <param name="onExit">Läuft beim Beenden vor der Verabschiedung, falls es ein Gespräch gab.</param>
    /// <param name="archive">Speichert das Gespräch nach jeder Antwort, damit es mit /weiter weitergehen kann.</param>
    public ChatLoop(IAnsiConsole console, IChatBackend backend, Func<DateTime> clock, CommandRegistry? commands = null, string? historyFile = null,
        string? opening = null, Func<Conversation, CancellationToken, Task>? onExit = null, ConversationArchive? archive = null)
    {
        _onExit = onExit;
        _archive = archive;
        _commands = commands ?? CommandRegistry.CreateDefault();
        var interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        _console = console;
        _backend = backend;
        _clock = clock;
        _view = new ChatView(console, animate: interactive);
        var editor = new LineEditor(new InputHistory(historyFile), () => _commands.Visible.Select(c => c.Name));
        _input = new InputBox(console, clock, fancy: interactive, editor);
        _menu = interactive ? new ChoiceMenu(console, clock) : null;
        if (opening is { Length: > 0 })
        {
            _conversation.AddAssistant(opening);
            _view.WriteMaxLine($"[{Theme.Tag(Theme.Text)}]{Markup.Escape(opening)}[/]");
            console.WriteLine();
        }
    }

    /// <summary>Das Gespräch dieser Sitzung.</summary>
    public Conversation Conversation => _conversation;

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
                    await ExitAsync();
                    return;
                }

                _input.Erase();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                _view.WriteUserMessage(line);

                if (CommandRegistry.IsCommand(line))
                {
                    if (await RunCommandAsync(line) == CommandResult.Exit)
                    {
                        await ExitAsync();
                        return;
                    }
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
            Save();
        }
    }

    private void Save()
    {
        try
        {
            _archive?.Save(_conversation);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Llm.LlmEngine.Log($"Gespräch nicht gespeichert: {e.Message}");
        }
    }

    private volatile bool _exiting;

    /// <summary>Vor dem Abschied: Max notiert sich das Wichtigste aus dem Gespräch (Strg+C überspringt das).</summary>
    private async Task ExitAsync()
    {
        _exiting = true;
        if (_onExit is not null && _conversation.Messages.Any(m => m.Role == ChatRole.User))
        {
            _console.MarkupLine($" [{Theme.Tag(Theme.Muted)}]✻ Ich notiere mir noch kurz das Wichtigste … (Strg+C überspringt)[/]");
            using var cts = new CancellationTokenSource();
            _reply = cts;
            _exiting = false;       // ab hier bricht Strg+C das Notieren ab
            try
            {
                await _onExit(_conversation, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                Llm.LlmEngine.Log($"Gedächtnis beim Beenden gescheitert: {e}");
            }
            finally
            {
                _reply = null;
                _exiting = true;
            }
        }
        ExitCommand.WriteFarewell(_console);
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

        if (_exiting)
            return;

        if (_ctrlC.Press(_clock()) == CtrlCPolicy.Action.Exit)
        {
            _input.MoveBelow();
            ExitAsync().GetAwaiter().GetResult();
            Environment.Exit(0);
        }

        _input.ShowHint("Nochmal Strg+C zum Beenden");
    }
}
