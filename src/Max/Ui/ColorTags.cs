using System.Text;
using Spectre.Console;

namespace Max.Ui;

/// <summary>Ein Stück Antworttext mit seiner Farbe (null = normale Textfarbe).</summary>
internal readonly record struct ColoredText(string Text, Color? Color);

/// <summary>
/// Versteht Max' Farb-Tags wie <c>{rot}Achtung{/rot}</c> im Antwort-Strom. Tags dürfen über
/// mehrere Stücke verteilt ankommen. Eigene geschweifte Tags statt Spectre-Markup, damit
/// eckige Klammern in Code (<c>arr[0]</c>) nie etwas kaputt machen. Unbekannte Tags bleiben Text.
/// </summary>
internal sealed class ColorTags
{
    private const int MaxTagLength = 12;

    private static readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rot"] = new Color(240, 80, 80),
        ["grün"] = new Color(90, 200, 120),
        ["gruen"] = new Color(90, 200, 120),
        ["gelb"] = new Color(230, 200, 80),
        ["blau"] = new Color(100, 150, 240),
        ["cyan"] = new Color(80, 200, 210),
        ["magenta"] = new Color(210, 110, 210),
        ["grau"] = Theme.Muted,
        ["akzent"] = Theme.Accent,
    };

    private readonly Stack<Color> _open = new();
    private readonly StringBuilder _pending = new();

    public static bool IsKnown(string name) => Colors.ContainsKey(name);

    public IEnumerable<ColoredText> Push(string chunk)
    {
        var output = new List<ColoredText>();
        var text = new StringBuilder();

        void Emit()
        {
            if (text.Length > 0)
                output.Add(new ColoredText(text.ToString(), _open.Count > 0 ? _open.Peek() : null));
            text.Clear();
        }

        foreach (var c in chunk)
        {
            if (_pending.Length == 0)
            {
                if (c == '{')
                    _pending.Append(c);
                else
                    text.Append(c);
                continue;
            }

            _pending.Append(c);
            if (c == '}')
            {
                var tag = _pending.ToString(1, _pending.Length - 2);
                _pending.Clear();
                if (TryApply(tag, Emit))
                    continue;
                text.Append('{').Append(tag).Append('}');
            }
            else if (!IsTagChar(c) || _pending.Length > MaxTagLength)
            {
                // Doch kein Tag (z. B. "{ x }" in Code) – als Text durchlassen.
                text.Append(_pending);
                _pending.Clear();
            }
        }

        Emit();
        return output;
    }

    /// <summary>Am Ende: ein angefangenes "{…" war kein Tag.</summary>
    public IEnumerable<ColoredText> Flush()
    {
        if (_pending.Length == 0)
            return [];
        var rest = new ColoredText(_pending.ToString(), _open.Count > 0 ? _open.Peek() : null);
        _pending.Clear();
        return [rest];
    }

    private bool TryApply(string tag, Action emit)
    {
        if (tag.StartsWith('/'))
        {
            if (!IsKnown(tag[1..]) || _open.Count == 0)
                return false;
            emit();
            _open.Pop();
            return true;
        }

        if (!Colors.TryGetValue(tag, out var color))
            return false;
        emit();
        _open.Push(color);
        return true;
    }

    private static bool IsTagChar(char c) => char.IsLetter(c) || c == '/';
}
