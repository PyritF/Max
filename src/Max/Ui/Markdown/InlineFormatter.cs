using System.Text;
using Spectre.Console;

namespace Max.Ui.Markdown;

/// <summary>
/// Formatierung innerhalb einer Zeile, Zeichen für Zeichen während des Streamings:
/// <c>**fett**</c>, <c>*kursiv*</c>, <c>`code`</c> und Max' Farb-Tags <c>{rot}…{/rot}</c>.
/// Mehrdeutige Zeichen (ein einzelnes <c>*</c>, ein angefangenes <c>{rot</c>) werden kurz
/// zurückgehalten, bis klar ist, was sie bedeuten. "3 * 4" bleibt Rechnung, "snake_case" bleibt Text.
/// </summary>
internal sealed class InlineFormatter(Action<string, Style> output)
{
    public static readonly Color CodeColor = new(230, 180, 120);
    private const int MaxTagLength = 12;

    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _run = new();
    private Style _runStyle = Style.Plain;
    private readonly Stack<Color> _colors = new();
    private Style _base = Style.Plain;
    private bool _bold, _italic, _code;
    private char _previous = ' ';

    /// <summary>Grundstil der Zeile (z. B. fett und farbig für Überschriften).</summary>
    public void StartLine(Style baseStyle)
    {
        EndLine();
        _base = baseStyle;
    }

    /// <summary>Nimmt Text an und gibt aus, was schon feststeht – zusammenhängend, damit Emojis heil bleiben.</summary>
    public void Push(string text)
    {
        foreach (var c in text)
            PushChar(c);
        FlushRun();
    }

    private void PushChar(char c)
    {
        if (_code)
        {
            if (c == '`')
                _code = false;
            else
                Emit(c);
            return;
        }

        if (_pending.Length > 0)
        {
            if (_pending[0] == '*')
            {
                _pending.Clear();
                if (c == '*')
                {
                    _bold = !_bold;
                    return;
                }
                ResolveItalic(next: c);
            }
            else if (_pending[0] == '\\')
            {
                _pending.Clear();
                if (!"*`{\\_#|".Contains(c))
                    Emit('\\');
                Emit(c);
                return;
            }
            else if (_pending[0] == '{')
            {
                _pending.Append(c);
                if (c == '}')
                {
                    var tag = _pending.ToString(1, _pending.Length - 2);
                    _pending.Clear();
                    if (!TryApplyTag(tag))
                        EmitLiteral("{" + tag + "}");
                }
                else if (!(char.IsLetter(c) || c == '/') || _pending.Length > MaxTagLength)
                {
                    // Doch kein Tag (z. B. "{ x }" in Code-Text) – als Text durchlassen.
                    var literal = _pending.ToString();
                    _pending.Clear();
                    EmitLiteral(literal);
                }
                return;
            }
        }

        switch (c)
        {
            case '*':
                _pending.Append(c);
                break;
            case '`':
                _code = true;
                break;
            case '{':
            case '\\':
                _pending.Append(c);
                break;
            default:
                Emit(c);
                break;
        }
    }

    /// <summary>Zeilenende: Zurückgehaltenes ausgeben, offene Formatierung schließen.</summary>
    public void EndLine()
    {
        if (_pending.Length > 0)
        {
            var pending = _pending.ToString();
            _pending.Clear();
            if (pending == "*")
                ResolveItalic(next: ' ');
            else
                EmitLiteral(pending);
        }
        FlushRun();
        _bold = _italic = _code = false;
        _colors.Clear();
        _previous = ' ';
    }

    /// <summary>Ganze Zeile auf einmal in Spectre-Markup umwandeln (für Tabellenzellen).</summary>
    public static string ToMarkup(string text, Style baseStyle)
    {
        var markup = new StringBuilder();
        var formatter = new InlineFormatter((segment, style) =>
        {
            var tag = style.ToMarkup();
            markup.Append(tag.Length == 0 ? Markup.Escape(segment) : $"[{tag}]{Markup.Escape(segment)}[/]");
        });
        formatter.StartLine(baseStyle);
        formatter.Push(text);
        formatter.EndLine();
        return markup.ToString();
    }

    /// <summary>Ein einzelnes "*": Kursiv auf/zu – oder einfach ein Sternchen (z. B. "3 * 4").</summary>
    private void ResolveItalic(char next)
    {
        if (_italic && !char.IsWhiteSpace(_previous))
            _italic = false;
        else if (!_italic && !char.IsWhiteSpace(next) && (char.IsWhiteSpace(_previous) || char.IsPunctuation(_previous)))
            _italic = true;
        else
            Emit('*');
    }

    private bool TryApplyTag(string tag)
    {
        if (tag.StartsWith('/'))
        {
            if (_colors.Count == 0 || !ColorTags.TryGet(tag[1..], out _))
                return false;
            _colors.Pop();
            return true;
        }
        if (!ColorTags.TryGet(tag, out var color))
            return false;
        _colors.Push(color);
        return true;
    }

    private void EmitLiteral(string text)
    {
        foreach (var c in text)
            Emit(c);
    }

    private void Emit(char c)
    {
        var decoration = _base.Decoration;
        if (_bold) decoration |= Decoration.Bold;
        if (_italic) decoration |= Decoration.Italic;
        var foreground = _code ? CodeColor : _colors.Count > 0 ? _colors.Peek() : _base.Foreground;
        var style = new Style(foreground, _base.Background, decoration);
        if (_run.Length > 0 && style != _runStyle)
            FlushRun();
        _runStyle = style;
        _run.Append(c);
        _previous = c;
    }

    private void FlushRun()
    {
        if (_run.Length == 0)
            return;
        output(_run.ToString(), _runStyle);
        _run.Clear();
    }
}
