using System.Text;

namespace Max.Llm;

/// <summary>
/// Entfernt <c>&lt;think&gt;…&lt;/think&gt;</c> aus dem Antwort-Strom – ein Sicherheitsnetz, falls das
/// Modell trotz leerem Denk-Block nachdenkt. Tags dürfen über mehrere Stücke verteilt ankommen.
/// </summary>
internal sealed class ThinkFilter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private readonly StringBuilder _pending = new();
    private bool _thinking;

    /// <summary>Ob unterwegs Denk-Text verworfen wurde.</summary>
    public bool DroppedAnything { get; private set; }

    /// <summary>Nimmt ein Stück an und liefert, was davon angezeigt werden darf (evtl. leer).</summary>
    public string Push(string chunk)
    {
        _pending.Append(chunk);
        var output = new StringBuilder();

        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            var tag = _thinking ? Close : Open;
            var index = text.IndexOf(tag, StringComparison.Ordinal);

            if (index >= 0)
            {
                if (!_thinking)
                    output.Append(text, 0, index);
                else
                    DroppedAnything = true;
                _pending.Remove(0, index + tag.Length);
                _thinking = !_thinking;
                if (!_thinking)
                    TrimLeadingNewlines();
                continue;
            }

            // Kein ganzes Tag: Alles ausgeben bzw. verwerfen – bis auf ein mögliches angefangenes Tag am Ende.
            var keep = PartialTagSuffix(text, tag);
            if (!_thinking)
                output.Append(text, 0, text.Length - keep);
            else if (text.Length > keep)
                DroppedAnything = true;
            _pending.Remove(0, text.Length - keep);
            break;
        }

        return output.ToString();
    }

    /// <summary>Am Ende: ein angefangenes Tag, das doch keins wurde, gehört zum Text.</summary>
    public string Flush()
    {
        var rest = _thinking ? "" : _pending.ToString();
        _pending.Clear();
        return rest;
    }

    private void TrimLeadingNewlines()
    {
        while (_pending.Length > 0 && _pending[0] is '\n' or '\r')
            _pending.Remove(0, 1);
    }

    /// <summary>Länge des längsten Endes von <paramref name="text"/>, das der Anfang von <paramref name="tag"/> ist.</summary>
    private static int PartialTagSuffix(string text, string tag)
    {
        for (var length = Math.Min(tag.Length - 1, text.Length); length > 0; length--)
        {
            if (text.AsSpan(text.Length - length).SequenceEqual(tag.AsSpan(0, length)))
                return length;
        }
        return 0;
    }
}
