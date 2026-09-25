using System.Text;
using Spectre.Console;

namespace Max.Ui;

/// <summary>Zeigt Nachrichten im Chat an – die eigenen und Max' gestreamte Antworten.</summary>
internal sealed class ChatView(IAnsiConsole console, bool animate)
{
    private const string Indent = "   ";
    private static readonly TimeSpan SpinnerInterval = TimeSpan.FromMilliseconds(80);

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
    /// Gibt Max' Antwort aus, sobald die Stücke eintreffen. Bis zum ersten Stück dreht sich
    /// ein Spinner. Bei Abbruch (Strg+C) wird "(abgebrochen)" angehängt.
    /// </summary>
    /// <returns>Der bis dahin empfangene Text – für den Gesprächsverlauf.</returns>
    public async Task<string> StreamReplyAsync(IAsyncEnumerable<string> chunks, CancellationToken ct)
    {
        var text = new StringBuilder();
        var style = new Style(Theme.Text);

        console.Markup($" [{Theme.Tag(Theme.Accent)}]{Theme.Symbol}[/] ");
        var column = Indent.Length;
        // Eine Spalte Luft zum Rand – volle Zeilen brechen in manchen Terminals doppelt um.
        var maxColumn = console.Profile.Width - 1;

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
                column = WriteWrapped(chunk, column, maxColumn, style);
            }
            await StopSpinnerAsync();
        }
        catch (OperationCanceledException)
        {
            await StopSpinnerAsync();
            var gap = text.Length > 0 && !char.IsWhiteSpace(text[^1]) ? " " : "";
            console.Markup($"[{Theme.Tag(Theme.Muted)}]{gap}(abgebrochen)[/]");
        }

        console.WriteLine();
        console.WriteLine();
        return text.ToString();
    }

    /// <summary>
    /// Schreibt ein Stück der Antwort mit eigenem Zeilenumbruch an Wortgrenzen, damit
    /// umgebrochene Zeilen bündig unter dem Text nach "◆" stehen. Gibt die neue Spalte zurück.
    /// </summary>
    private int WriteWrapped(string chunk, int column, int maxColumn, Style style)
    {
        var parts = chunk.Split('\n');
        for (var p = 0; p < parts.Length; p++)
        {
            if (p > 0)
            {
                console.Write("\n" + Indent);
                column = Indent.Length;
            }

            foreach (var word in SplitKeepingSpaces(parts[p]))
            {
                if (column + word.TrimEnd().Length > maxColumn && column > Indent.Length)
                {
                    console.Write("\n" + Indent);
                    column = Indent.Length;
                    if (string.IsNullOrWhiteSpace(word))
                        continue;
                }

                console.Write(word, style);
                column += word.Length;
            }
        }

        return column;
    }

    /// <summary>"Hallo schöne Welt" → "Hallo ", "schöne ", "Welt".</summary>
    private static IEnumerable<string> SplitKeepingSpaces(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ' ')
                continue;
            yield return text[start..(i + 1)];
            start = i + 1;
        }
        if (start < text.Length)
            yield return text[start..];
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
