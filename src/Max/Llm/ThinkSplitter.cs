using System.Text;

namespace Max.Llm;

/// <summary>
/// Trennt den Antwort-Strom in Nachdenken (<c>&lt;think&gt;…&lt;/think&gt;</c>) und Antwort.
/// Tags dürfen über mehrere Stücke verteilt ankommen. Beginnt die Erzeugung schon im Denk-Block
/// (der Prompt endet mit <c>&lt;think&gt;</c>), startet der Splitter im Denk-Modus.
/// </summary>
internal sealed class ThinkSplitter(bool startInThinking = false)
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private readonly StringBuilder _pending = new();
    private bool _answerStarted;

    public bool Thinking { get; private set; } = startInThinking;

    /// <summary>Wird wahr, sobald ein Denk-Block geschlossen wurde.</summary>
    public bool ThinkingEnded { get; private set; }

    /// <summary>Nimmt ein Stück an und liefert die fertigen Teile (Denken bzw. Antwort, evtl. keine).</summary>
    public List<(bool Thinking, string Text)> Push(string chunk)
    {
        _pending.Append(chunk);
        var output = new List<(bool, string)>();

        while (_pending.Length > 0)
        {
            var text = _pending.ToString();
            var tag = Thinking ? Close : Open;
            var index = text.IndexOf(tag, StringComparison.Ordinal);

            if (index >= 0)
            {
                Emit(output, text[..index]);
                _pending.Remove(0, index + tag.Length);
                Thinking = !Thinking;
                if (!Thinking)
                {
                    ThinkingEnded = true;
                    TrimLeadingNewlines();
                }
                continue;
            }

            // Kein ganzes Tag: alles ausgeben – bis auf ein mögliches angefangenes Tag am Ende.
            var keep = PartialTagSuffix(text, tag);
            Emit(output, text[..^keep]);
            _pending.Remove(0, text.Length - keep);
            break;
        }

        return output;
    }

    /// <summary>Am Ende: ein angefangenes Tag, das doch keins wurde, gehört zum Text.</summary>
    public List<(bool Thinking, string Text)> Flush()
    {
        var output = new List<(bool, string)>();
        Emit(output, _pending.ToString());
        _pending.Clear();
        return output;
    }

    private void Emit(List<(bool, string)> output, string text)
    {
        // Führende Leerzeilen am Anfang der Antwort bzw. des Nachdenkens sehen nach einem Fehler aus.
        if (!Thinking && !_answerStarted)
        {
            text = text.TrimStart('\n', '\r', ' ');
            _answerStarted = text.Length > 0;
        }
        if (text.Length > 0)
            output.Add((Thinking, text));
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
