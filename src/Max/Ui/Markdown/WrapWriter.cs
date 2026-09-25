using System.Globalization;
using Spectre.Console;

namespace Max.Ui.Markdown;

/// <summary>
/// Schreibt Max' Antwort mit eigenem Zeilenumbruch an Wortgrenzen, eingerückt unter dem Text nach "◆".
/// Ein Wort wird erst geschrieben, wenn es vollständig ist – Stücke vom Modell enden oft mitten im Wort.
/// Zeilen können ein Präfix haben (z. B. "• " bei Listen, "│ " bei Code); umgebrochene Folgezeilen
/// bekommen das Folge-Präfix, damit alles bündig bleibt. Breiten zählen in Terminal-Zellen.
/// </summary>
internal sealed class WrapWriter
{
    /// <summary>Wo der Cursor gerade steht.</summary>
    private enum LineState
    {
        /// <summary>Am Anfang der Antwort, direkt hinter " ◆ ".</summary>
        Start,
        /// <summary>Mitten in einer Zeile.</summary>
        Open,
        /// <summary>Zeile beendet, der Zeilenumbruch steht noch aus (wird erst geschrieben, wenn noch etwas kommt).</summary>
        Ended,
        /// <summary>Ganz links in einer neuen Zeile (nach einer Tabelle, die Spectre selbst gezeichnet hat).</summary>
        Column0,
    }

    private readonly IAnsiConsole _console;
    private readonly int _indent;
    private readonly List<(string Text, Style Style)> _word = [];
    private int _wordWidth;
    private int _column;
    private LineState _state = LineState.Start;
    private bool _lastWasBlank = true; // am Anfang keine Leerzeilen
    private IReadOnlyList<(string Text, Style Style)> _continuation = [];
    private int _continuationWidth;

    public WrapWriter(IAnsiConsole console, int indent)
    {
        _console = console;
        _indent = indent;
        _column = indent;
    }

    /// <summary>Eine Spalte Luft zum Rand – volle Zeilen brechen in manchen Terminals doppelt um.</summary>
    public int MaxColumn => Math.Max(_indent + 10, _console.Profile.Width - 1);

    /// <summary>Platz für Text in einer Zeile (ohne Einrückung).</summary>
    public int LineWidth => MaxColumn - _indent;

    /// <summary>
    /// Beginnt eine neue Zeile mit Präfix. <paramref name="continuation"/> steht vor umgebrochenen Folgezeilen.
    /// </summary>
    public void StartLine(IReadOnlyList<(string Text, Style Style)> prefix, IReadOnlyList<(string Text, Style Style)> continuation)
    {
        FlushWord();
        if (_state == LineState.Open)
            _state = LineState.Ended;
        BeginLine();
        foreach (var (text, style) in prefix)
            WriteDirect(text, style);
        _continuation = continuation;
        _continuationWidth = continuation.Sum(p => Width(p.Text));
    }

    /// <summary>Text mit Umbruch an Wortgrenzen.</summary>
    public void Write(string text, Style style)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element is "\n" or "\r\n")
            {
                EndLine();
                continue;
            }
            if (element == "\r")
                continue;

            if (_state != LineState.Open)
                StartLine([], []);

            if (element == " ")
            {
                FlushWord();
                if (_column + 1 > MaxColumn)
                    WrapLine(); // Leerzeichen am Zeilenende entfällt
                else
                    WriteDirect(" ", style);
            }
            else
            {
                AddToWord(element, style);
            }
        }
    }

    /// <summary>Beendet die aktuelle Zeile (der Umbruch wird erst geschrieben, wenn noch etwas folgt).</summary>
    public void EndLine()
    {
        FlushWord();
        if (_state == LineState.Open)
            _state = LineState.Ended;
        _continuation = [];
        _continuationWidth = 0;
    }

    /// <summary>Eine Leerzeile – mehrere hintereinander werden zu einer.</summary>
    public void BlankLine()
    {
        EndLine();
        if (_lastWasBlank || _state == LineState.Start)
            return;
        if (_state is LineState.Ended or LineState.Column0)
            _console.Write("\n"); // nach Column0 (Tabelle) bleibt der Cursor danach ganz links
        _lastWasBlank = true;
    }

    /// <summary>
    /// Übergibt an etwas, das Spectre selbst zeichnet (Tabelle, Trennlinie): Die aktuelle Zeile wird
    /// beendet, danach steht der Cursor ganz links. Nach dem Zeichnen <see cref="AfterExternalBlock"/>.
    /// </summary>
    public void BeforeExternalBlock()
    {
        FlushWord();
        if (_state != LineState.Column0)
            _console.Write("\n");
        _state = LineState.Column0;
    }

    public void AfterExternalBlock()
    {
        _state = LineState.Column0;
        _lastWasBlank = false;
    }

    /// <summary>Den Rest ausgeben – am Ende oder bei Abbruch.</summary>
    public void Finish() => FlushWord();

    /// <summary>Schließt die Antwort mit einer Leerzeile darunter ab.</summary>
    public void CloseReply() => _console.Write(_state == LineState.Column0 ? "\n" : "\n\n");

    private void BeginLine()
    {
        switch (_state)
        {
            case LineState.Ended:
                _console.Write("\n" + new string(' ', _indent));
                break;
            case LineState.Column0:
                _console.Write(new string(' ', _indent));
                break;
        }
        _column = _indent;
        _state = LineState.Open;
        _lastWasBlank = false;
    }

    private void AddToWord(string element, Style style)
    {
        if (_word.Count > 0 && _word[^1].Style == style)
            _word[^1] = (_word[^1].Text + element, style);
        else
            _word.Add((element, style));
        _wordWidth += Width(element);
    }

    private void FlushWord()
    {
        if (_word.Count == 0)
            return;

        var lineStart = _indent + _continuationWidth;
        if (_column + _wordWidth > MaxColumn && _column > lineStart)
            WrapLine();

        if (_wordWidth <= MaxColumn - lineStart)
        {
            foreach (var (text, style) in _word)
                WriteDirect(text, style);
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
                    if (_column + Width(element) > MaxColumn)
                        WrapLine();
                    WriteDirect(element, style);
                }
            }
        }

        _word.Clear();
        _wordWidth = 0;
    }

    private void WrapLine()
    {
        _console.Write("\n" + new string(' ', _indent));
        _column = _indent;
        foreach (var (text, style) in _continuation)
            WriteDirect(text, style);
    }

    private void WriteDirect(string text, Style style)
    {
        _console.Write(text, style);
        _column += Width(text);
    }

    private static int Width(string text)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            width += CellWidth.Of(elements.GetTextElement());
        return width;
    }
}
