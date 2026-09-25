using System.Globalization;
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
    /// ein Spinner. Farb-Tags wie <c>{rot}…{/rot}</c> werden angewendet. Bei Abbruch (Strg+C)
    /// wird "(abgebrochen)" angehängt.
    /// </summary>
    /// <returns>Der bis dahin empfangene Text (mit Farb-Tags) – für den Gesprächsverlauf.</returns>
    public async Task<string> StreamReplyAsync(IAsyncEnumerable<string> chunks, CancellationToken ct)
    {
        var text = new StringBuilder();
        var colors = new ColorTags();
        var writer = new WrapWriter(console, Indent.Length);

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
                foreach (var part in colors.Push(chunk))
                    writer.Write(part);
            }
            await StopSpinnerAsync();
            foreach (var part in colors.Flush())
                writer.Write(part);
            writer.Finish();
        }
        catch (OperationCanceledException)
        {
            await StopSpinnerAsync();
            writer.Finish();
            var gap = text.Length > 0 && !char.IsWhiteSpace(text[^1]) ? " " : "";
            console.Markup($"[{Theme.Tag(Theme.Muted)}]{gap}(abgebrochen)[/]");
        }
        finally
        {
            if (animate)
                console.Cursor.Show(true);
        }

        console.WriteLine();
        console.WriteLine();
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

    /// <summary>
    /// Schreibt Text mit eigenem Zeilenumbruch an Wortgrenzen, bündig unter dem Text nach "◆".
    /// Ein Wort wird erst geschrieben, wenn es vollständig ist – Stücke vom Modell enden oft mitten
    /// im Wort. Breiten zählen in Terminal-Zellen (Emojis und breite Zeichen = 2).
    /// </summary>
    internal sealed class WrapWriter
    {
        private readonly IAnsiConsole _console;
        private readonly int _indent;
        private readonly List<(string Text, Style Style)> _word = [];
        private int _wordWidth;
        private int _column;

        /// <param name="indent">Einrückung der Folgezeilen; die erste Zeile beginnt direkt dort (hinter " ◆ ").</param>
        public WrapWriter(IAnsiConsole console, int indent)
        {
            _console = console;
            _indent = indent;
            _column = indent;
        }

        private static readonly Style Normal = new(Theme.Text);

        /// <summary>Eine Spalte Luft zum Rand – volle Zeilen brechen in manchen Terminals doppelt um.</summary>
        private int MaxColumn => Math.Max(_indent + 10, _console.Profile.Width - 1);

        public void Write(ColoredText part)
        {
            var style = part.Color is { } color ? new Style(color) : Normal;
            var elements = StringInfo.GetTextElementEnumerator(part.Text);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                switch (element)
                {
                    case "\n" or "\r\n":
                        FlushWord();
                        NewLine();
                        break;
                    case "\r":
                        break;
                    case " ":
                        FlushWord();
                        if (_column + 1 > MaxColumn)
                            NewLine(); // Leerzeichen am Zeilenende entfällt
                        else
                        {
                            _console.Write(" ", style);
                            _column++;
                        }
                        break;
                    default:
                        AddToWord(element, style);
                        break;
                }
            }
        }

        /// <summary>Den Rest ausgeben – am Ende oder bei Abbruch.</summary>
        public void Finish() => FlushWord();

        private void AddToWord(string element, Style style)
        {
            if (_word.Count > 0 && _word[^1].Style == style)
                _word[^1] = (_word[^1].Text + element, style);
            else
                _word.Add((element, style));
            _wordWidth += CellWidth.Of(element);
        }

        private void FlushWord()
        {
            if (_word.Count == 0)
                return;

            var available = MaxColumn - _indent;
            if (_column + _wordWidth > MaxColumn && _column > _indent)
                NewLine();

            if (_wordWidth <= available)
            {
                foreach (var (text, style) in _word)
                    _console.Write(text, style);
                _column += _wordWidth;
            }
            else
            {
                // Länger als eine ganze Zeile (z. B. eine URL): hart teilen.
                foreach (var (text, style) in _word)
                {
                    var elements = StringInfo.GetTextElementEnumerator(text);
                    while (elements.MoveNext())
                    {
                        var element = elements.GetTextElement();
                        var width = CellWidth.Of(element);
                        if (_column + width > MaxColumn)
                            NewLine();
                        _console.Write(element, style);
                        _column += width;
                    }
                }
            }

            _word.Clear();
            _wordWidth = 0;
        }

        private void NewLine()
        {
            _console.Write("\n" + new string(' ', _indent));
            _column = _indent;
        }
    }
}
