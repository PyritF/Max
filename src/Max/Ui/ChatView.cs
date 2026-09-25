using System.Text;
using Max.Ui.Markdown;
using Spectre.Console;

namespace Max.Ui;

/// <summary>Zeigt Nachrichten im Chat an – die eigenen und Max' gestreamte Antworten.</summary>
internal sealed class ChatView(IAnsiConsole console, bool animate)
{
    private const string Indent = "   ";
    private static readonly TimeSpan SpinnerInterval = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// Die Rückfrage (<c>```frage</c>) aus der letzten Antwort, falls es eine gab.
    /// Mit Animation zeigt der Aufrufer dafür ein Auswahlmenü, sonst stehen die Antworten als Liste im Text.
    /// </summary>
    public ChoiceQuestion? LastQuestion { get; private set; }

    public void ClearQuestion() => LastQuestion = null;

    public void SetQuestion(ChoiceQuestion question) => LastQuestion = question;

    /// <summary>Die eigene Nachricht als graue Verlaufszeile.</summary>
    public void WriteUserMessage(string text)
    {
        console.MarkupLine($" [{Theme.Tag(Theme.Muted)}]›[/] [{Theme.Tag(Theme.Dim)}]{Markup.Escape(text)}[/]");
        console.WriteLine();
    }

    /// <summary>Eine kurze Mitteilung von Max, z. B. zu einem unbekannten Befehl.</summary>
    public void WriteMaxLine(string markup)
    {
        console.MarkupLine($" [{Theme.Tag(Theme.Accent)}]{Theme.Symbol}[/] {markup}");
        console.WriteLine();
    }

    /// <summary>
    /// Gibt Max' Antwort aus, sobald die Stücke eintreffen – als formatiertes Markdown mit
    /// Farb-Tags wie <c>{rot}…{/rot}</c>. Bis zum ersten Stück dreht sich ein Spinner.
    /// Bei Abbruch (Strg+C) wird "(abgebrochen)" angehängt.
    /// </summary>
    /// <returns>Der bis dahin empfangene Rohtext – für den Gesprächsverlauf.</returns>
    public async Task<string> StreamReplyAsync(IAsyncEnumerable<string> chunks, CancellationToken ct)
    {
        var text = new StringBuilder();
        var writer = new WrapWriter(console, Indent.Length);
        var markdown = new MarkdownRenderer(console, writer, listQuestionOptions: !animate);
        LastQuestion = null;

        console.Markup($" [{Theme.Tag(Theme.Accent)}]{Theme.Symbol}[/] ");

        // Der Cursor bleibt unsichtbar, solange Max denkt und schreibt – sonst springt er mit dem Spinner hin und her.
        if (animate)
            console.Cursor.Show(false);

        using var spinnerStop = new CancellationTokenSource();
        var spinner = animate ? SpinAsync(spinnerStop.Token) : Task.CompletedTask;

        async Task StopSpinnerAsync()
        {
            if (spinnerStop.IsCancellationRequested)
                return;
            await spinnerStop.CancelAsync();
            await spinner;
        }

        try
        {
            await foreach (var chunk in chunks.WithCancellation(ct))
            {
                await StopSpinnerAsync();
                text.Append(chunk);
                markdown.Push(chunk);
            }
            await StopSpinnerAsync();
            markdown.Finish();
            LastQuestion = markdown.Question;
        }
        catch (OperationCanceledException)
        {
            await StopSpinnerAsync();
            markdown.Finish();
            var gap = text.Length > 0 && !char.IsWhiteSpace(text[^1]) ? " " : "";
            writer.Write(gap, Style.Plain);
            writer.Write("(abgebrochen)", new Style(Theme.Muted));
            writer.Finish();
        }
        finally
        {
            if (animate)
                console.Cursor.Show(true);
        }

        writer.CloseReply();
        return text.ToString();
    }

    /// <summary>Dreht den Spinner an Ort und Stelle: Zeichen schreiben, per Backspace zurück.</summary>
    private async Task SpinAsync(CancellationToken stop)
    {
        var frames = console.Profile.Capabilities.Unicode ? Spinner.Known.Dots.Frames : Spinner.Known.Ascii.Frames;
        var accent = new Style(Theme.Accent);
        var i = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                console.Write(frames[i++ % frames.Count], accent);
                await Task.Delay(SpinnerInterval, stop);
                console.Write("\b");
            }
        }
        catch (OperationCanceledException)
        {
            console.Write("\b");
        }
        // Das zuletzt gezeichnete Zeichen überschreiben und wieder zurück.
        console.Write(" \b");
    }
}
