using System.Text;

namespace Max.Llm;

/// <summary>
/// Hält Absätze zurück, die mit einer Angebots-Floskel beginnen („Möchtest du …“, „Sag Bescheid …“).
/// Geht die Antwort danach weiter, kommt der Absatz nach; steht er am Ende, fällt er weg – und damit auch
/// aus dem Verlauf, sonst gewöhnt sich das Modell die Floskel im Gespräch an. Besteht die ganze Antwort nur
/// aus so einem Absatz, ist es eine echte Rückfrage und bleibt. Code-Blöcke bleiben unberührt.
/// </summary>
internal sealed class ClosingFilter
{
    internal static readonly string[] Phrases =
    [
        "Möchtest du", "Willst du", "Soll ich", "Brauchst du", "Hast du noch",
        "Wenn du noch", "Wenn du mir", "Wenn du magst", "Wenn du willst", "Wenn du möchtest",
        "Falls du noch", "Falls du mehr", "Falls du weitere",
        "Sag Bescheid", "Sag einfach Bescheid", "Sag mir Bescheid", "Lass mich wissen",
        "Gibt es noch", "Kann ich dir noch",
    ];

    private readonly StringBuilder _line = new();   // Anfang einer Absatz-Zeile, noch nicht entschieden
    private readonly StringBuilder _held = new();   // zurückgehaltene Floskel-Absätze
    private readonly StringBuilder _shownLine = new();
    private bool _atLineStart = true;
    private bool _paragraphStart = true;            // Zeile beginnt einen Absatz (Anfang oder nach Leerzeile)
    private bool _deciding;
    private bool _holding;
    private bool _inFence;
    private bool _anyShown;

    public string Push(string text)
    {
        var output = new StringBuilder();
        foreach (var c in text)
            Add(c, output);
        return output.ToString();
    }

    /// <summary>Ende der Antwort: Zurückgehaltenes nur zeigen, wenn es die ganze Antwort ist.</summary>
    public string Flush()
    {
        var output = new StringBuilder();
        if (_deciding)
        {
            var line = _line.ToString();
            _line.Clear();
            _deciding = false;
            if (StartsWithPhrase(line, complete: true) == true)
                _held.Append(line);
            else
                Emit(line, output);
        }
        if (_held.Length > 0)
        {
            if (!_anyShown)
                output.Append(_held);
            else
                LlmEngine.Log($"Floskel am Ende weggelassen: {_held.ToString().Trim().ReplaceLineEndings(" ")}");
            _held.Clear();
        }
        _holding = false;
        return output.ToString();
    }

    private void Add(char c, StringBuilder output)
    {
        if (_deciding)
        {
            _line.Append(c);
            var line = _line.ToString();
            switch (StartsWithPhrase(line, complete: c == '\n'))
            {
                case true:
                    _deciding = false;
                    _holding = true;
                    _held.Append(line);
                    _line.Clear();
                    break;
                case false:
                    _deciding = false;
                    _line.Clear();
                    Release(output);
                    Emit(line, output);
                    break;
            }
            if (c == '\n')
                NewLine(line.Trim().Length == 0);
            return;
        }

        if (_atLineStart && !_inFence)
        {
            if (c == '\n')
            {
                (_holding ? _held : output).Append(c);
                NewLine(blank: true);
                return;
            }
            // In einem zurückgehaltenen Absatz zählt jede Zeile; sonst nur Zeilen am Absatz-Anfang.
            if ((_paragraphStart || _holding) && !char.IsWhiteSpace(c))
            {
                _atLineStart = false;
                _deciding = true;
                Add(c, output);
                return;
            }
        }

        if (_holding)
        {
            // Rest der Floskel-Zeile oder Leerzeichen am Zeilenanfang: weiter zurückhalten.
            _held.Append(c);
            if (c == '\n')
                NewLine(blank: false);
            return;
        }

        _atLineStart = false;
        Emit(c.ToString(), output);
        if (c == '\n')
            NewLine(blank: false);
    }

    private void NewLine(bool blank)
    {
        _atLineStart = true;
        _paragraphStart = blank || _paragraphStart && _holding;
    }

    private void Release(StringBuilder output)
    {
        if (_held.Length == 0)
        {
            _holding = false;
            return;
        }
        var held = _held.ToString();
        _held.Clear();
        _holding = false;
        Emit(held, output);
    }

    private void Emit(string text, StringBuilder output)
    {
        output.Append(text);
        if (text.Trim().Length > 0)
            _anyShown = true;
        // Code-Blöcke erkennen, damit darin nichts zurückgehalten wird.
        foreach (var c in text)
        {
            if (c != '\n')
            {
                _shownLine.Append(c);
                continue;
            }
            if (_shownLine.ToString().TrimStart().StartsWith("```", StringComparison.Ordinal))
                _inFence = !_inFence;
            _shownLine.Clear();
        }
    }

    /// <summary>true = Floskel, false = sicher keine, null = noch zu kurz, um es zu sagen.</summary>
    internal static bool? StartsWithPhrase(string line, bool complete)
    {
        var text = line.TrimStart().TrimStart('*', '_', '>', ' ');
        var undecided = false;
        foreach (var phrase in Phrases)
        {
            if (text.Length > phrase.Length)
            {
                if (text.StartsWith(phrase, StringComparison.OrdinalIgnoreCase) && !char.IsLetter(text[phrase.Length]))
                    return true;
            }
            else if (!complete && phrase.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                undecided = true;
            }
            else if (complete && text.TrimEnd().Equals(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return undecided ? null : false;
    }
}
