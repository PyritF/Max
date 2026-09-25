using System.Text;
using Spectre.Console;

namespace Max.Ui.Markdown;

/// <summary>
/// Formatierung innerhalb einer Zeile, Zeichen für Zeichen während des Streamings:
/// <c>**fett**</c>, <c>*kursiv*</c>, <c>`code`</c>, Max' Farb-Tags <c>{rot}…{/rot}</c>,
/// Farbverläufe <c>{verlauf}…{/verlauf}</c> und Emoji-Kürzel wie <c>:rocket:</c>.
/// Mehrdeutige Zeichen (ein einzelnes <c>*</c>, ein angefangenes <c>{rot</c>) werden kurz
/// zurückgehalten, bis klar ist, was sie bedeuten. "3 * 4" bleibt Rechnung, "snake_case" bleibt Text.
/// </summary>
internal sealed class InlineFormatter(Action<string, Style> output)
{
    public static readonly Color CodeColor = new(230, 180, 120);
    private const int MaxTagLength = 24;
    private const int MaxEmojiLength = 32;

    private readonly StringBuilder _pending = new();
    private readonly StringBuilder _run = new();
    private Style _runStyle = Style.Plain;
    private readonly Stack<Color> _colors = new();
    private Style _base = Style.Plain;
    // Offener Farbverlauf: Die Zeichen werden gesammelt und beim Schließen eingefärbt – erst dann ist die Länge bekannt.
    private (Color From, Color To)? _gradient;
    private readonly List<(char Char, Decoration Decoration)> _gradientText = [];
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
            else if (_pending[0] == ':')
            {
                if (c == ':')
                {
                    var name = _pending.ToString(1, _pending.Length - 1);
                    _pending.Clear();
                    var emoji = name.Length > 0 ? Emoji.Replace($":{name}:") : null;
                    if (emoji is not null && emoji != $":{name}:")
                    {
                        EmitLiteral(emoji);
                        return;
                    }
                    EmitLiteral(":" + name);
                    _pending.Append(':'); // der Doppelpunkt könnte ein neues Kürzel beginnen
                    return;
                }
                var validChar = char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '-';
                if (validChar && _pending.Length < MaxEmojiLength)
                {
                    _pending.Append(c);
                    return;
                }
                // Doch kein Kürzel ("Hinweis: …", "12:30"): alles als Text, das aktuelle Zeichen normal weiter.
                var literal = _pending.ToString();
                _pending.Clear();
                EmitLiteral(literal);
                PushChar(c);
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
                else if (!(char.IsLetter(c) || c is '/' or ':' or '-') || _pending.Length > MaxTagLength)
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
            case ':' when char.IsWhiteSpace(_previous) || _previous == '(':
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
        FlushGradient();
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
        if (tag.Equals("/verlauf", StringComparison.OrdinalIgnoreCase))
        {
            if (_gradient is null)
                return false;
            FlushGradient();
            return true;
        }
        if (tag.StartsWith("verlauf", StringComparison.OrdinalIgnoreCase) && _gradient is null)
        {
            var spec = tag.Length > 8 && tag[7] == ':' ? tag[8..] : null;
            _gradient = Widgets.ChartColors.Gradient(spec);
            return true;
        }

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

        if (_gradient is not null && !_code)
        {
            _gradientText.Add((c, decoration));
            _previous = c;
            return;
        }

        var foreground = _code ? CodeColor : _colors.Count > 0 ? _colors.Peek() : _base.Foreground;
        EmitStyled(c, new Style(foreground, _base.Background, decoration));
    }

    /// <summary>Den gesammelten Verlauf-Text einfärben: von der Start- zur Endfarbe, Zeichen für Zeichen.</summary>
    private void FlushGradient()
    {
        if (_gradient is not { } gradient)
            return;
        _gradient = null;
        var letters = _gradientText.Count(t => !char.IsWhiteSpace(t.Char));
        var index = 0;
        foreach (var (c, decoration) in _gradientText)
        {
            var t = letters <= 1 ? 0f : (float)index / (letters - 1);
            if (!char.IsWhiteSpace(c))
                index++;
            EmitStyled(c, new Style(Theme.Blend(gradient.From, gradient.To, t), _base.Background, decoration));
        }
        _gradientText.Clear();
    }

    private void EmitStyled(char c, Style style)
    {
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
