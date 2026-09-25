using System.Text;

namespace Max.Ui;

/// <summary>
/// Die Logik der Eingabezeile, ohne Terminal: Text, Cursor und was jede Taste bewirkt.
/// Mehrzeilig: Shift+Enter, Alt+Enter und "\" + Enter fügen einen Zeilenumbruch ein.
/// Eingefügter Text mit Zeilenumbrüchen wird nicht zeilenweise abgeschickt: Kommen die Tasten
/// als schneller Schwall (weitere liegen schon bereit), ist Enter ein Umbruch und kein Senden.
/// </summary>
internal sealed class LineEditor(InputHistory history, Func<IEnumerable<string>> commandNames)
{
    public enum Outcome { Continue, Submit }

    private readonly StringBuilder _text = new();
    private bool _burst;

    public string Text => _text.ToString();
    public int Cursor { get; private set; }

    /// <summary>Kurzer Hinweis für die Statuszeile (z. B. mehrere Treffer bei Tab), bis zur nächsten Taste.</summary>
    public string? Hint { get; private set; }

    public void Reset()
    {
        _text.Clear();
        Cursor = 0;
        Hint = null;
        _burst = false;
        history.ResetNavigation();
    }

    /// <param name="key">Die gedrückte Taste.</param>
    /// <param name="moreKeysPending">Liegen schon weitere Tasten bereit? (Zeichen für Einfügen)</param>
    public Outcome Handle(ConsoleKeyInfo key, bool moreKeysPending)
    {
        var inBurst = moreKeysPending || _burst;
        _burst = moreKeysPending;
        Hint = null;

        var ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        var shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);
        var alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if (shift || alt || inBurst || key.KeyChar == '\n' && ctrl)
                {
                    Insert("\n");
                    return Outcome.Continue;
                }
                if (Cursor > 0 && _text[Cursor - 1] == '\\' && (Cursor == _text.Length || _text[Cursor] == '\n'))
                {
                    _text.Remove(Cursor - 1, 1);
                    Cursor--;
                    Insert("\n");
                    return Outcome.Continue;
                }
                return Outcome.Submit;

            case ConsoleKey.Backspace:
                if (ctrl)
                    DeleteWordBefore();
                else if (Cursor > 0)
                {
                    var length = PreviousElementLength();
                    _text.Remove(Cursor - length, length);
                    Cursor -= length;
                }
                return Outcome.Continue;

            case ConsoleKey.Delete:
                if (Cursor < _text.Length)
                    _text.Remove(Cursor, NextElementLength());
                return Outcome.Continue;

            case ConsoleKey.LeftArrow:
                Cursor = ctrl ? WordStartBefore(Cursor) : Cursor - PreviousElementLength();
                return Outcome.Continue;

            case ConsoleKey.RightArrow:
                Cursor = ctrl ? WordEndAfter(Cursor) : Cursor + NextElementLength();
                return Outcome.Continue;

            case ConsoleKey.Home:
                Cursor = LineStart(Cursor);
                return Outcome.Continue;

            case ConsoleKey.End:
                Cursor = LineEnd(Cursor);
                return Outcome.Continue;

            case ConsoleKey.UpArrow:
                if (LineStart(Cursor) > 0)
                    MoveVertical(-1);
                else if (history.Previous(Text) is { } previous)
                    SetText(previous);
                return Outcome.Continue;

            case ConsoleKey.DownArrow:
                if (LineEnd(Cursor) < _text.Length)
                    MoveVertical(+1);
                else if (history.Next() is { } next)
                    SetText(next);
                return Outcome.Continue;

            case ConsoleKey.Escape:
                SetText("");
                history.ResetNavigation();
                return Outcome.Continue;

            case ConsoleKey.Tab when !inBurst:
                Complete();
                return Outcome.Continue;
        }

        if (ctrl && key.Key == ConsoleKey.W)
        {
            DeleteWordBefore();
            return Outcome.Continue;
        }

        var c = key.KeyChar;
        if (c == '\t')
            Insert("    ");
        else if (c is '\r' or '\n')
            Insert("\n"); // Zeilenumbruch aus eingefügtem Text, der nicht als Enter ankam
        else if (!char.IsControl(c) || char.IsSurrogate(c))
            Insert(c.ToString());
        return Outcome.Continue;
    }

    public void SetText(string text)
    {
        _text.Clear().Append(text);
        Cursor = _text.Length;
    }

    private void Insert(string s)
    {
        _text.Insert(Cursor, s);
        Cursor += s.Length;
    }

    /// <summary>Befehle ergänzen: "/he" → "/help ". Bei mehreren Treffern: gemeinsamer Anfang + Liste als Hinweis.</summary>
    private void Complete()
    {
        var text = Text;
        if (!text.StartsWith('/') || text.Contains(' ') || text.Contains('\n') || Cursor != text.Length)
            return;

        var prefix = text[1..].ToLowerInvariant();
        var matches = commandNames().Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Order().ToList();
        if (matches.Count == 1)
        {
            SetText("/" + matches[0] + " ");
        }
        else if (matches.Count > 1)
        {
            var common = matches.Aggregate((a, b) => new string(a.Zip(b).TakeWhile(p => p.First == p.Second).Select(p => p.First).ToArray()));
            SetText("/" + common);
            Hint = string.Join("  ", matches.Select(m => "/" + m));
        }
    }

    private void MoveVertical(int direction)
    {
        var column = Cursor - LineStart(Cursor);
        var target = direction < 0 ? LineStart(Cursor) - 1 : LineEnd(Cursor) + 1;
        var start = LineStart(target);
        Cursor = Math.Min(start + column, LineEnd(target));
    }

    private int LineStart(int index)
    {
        while (index > 0 && _text[index - 1] != '\n')
            index--;
        return index;
    }

    private int LineEnd(int index)
    {
        while (index < _text.Length && _text[index] != '\n')
            index++;
        return index;
    }

    private int WordStartBefore(int index)
    {
        while (index > 0 && char.IsWhiteSpace(_text[index - 1]))
            index--;
        while (index > 0 && !char.IsWhiteSpace(_text[index - 1]))
            index--;
        return index;
    }

    private int WordEndAfter(int index)
    {
        while (index < _text.Length && char.IsWhiteSpace(_text[index]))
            index++;
        while (index < _text.Length && !char.IsWhiteSpace(_text[index]))
            index++;
        return index;
    }

    private void DeleteWordBefore()
    {
        var start = WordStartBefore(Cursor);
        _text.Remove(start, Cursor - start);
        Cursor = start;
    }

    // Emojis bestehen aus zwei UTF-16-Zeichen – Cursor und Löschen behandeln sie als eins.
    private int PreviousElementLength() =>
        Cursor >= 2 && char.IsLowSurrogate(_text[Cursor - 1]) && char.IsHighSurrogate(_text[Cursor - 2]) ? 2 : Cursor > 0 ? 1 : 0;

    private int NextElementLength() =>
        Cursor + 1 < _text.Length && char.IsHighSurrogate(_text[Cursor]) && char.IsLowSurrogate(_text[Cursor + 1]) ? 2 : Cursor < _text.Length ? 1 : 0;
}
