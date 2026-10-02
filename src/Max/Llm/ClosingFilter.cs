using System.Text;
using System.Text.RegularExpressions;

namespace Max.Llm;

/// <summary>
/// Hält Absätze zurück, die mit einer Angebots-Floskel beginnen („Möchtest du …“, „Sag Bescheid …“).
/// Geht die Antwort danach weiter, kommt der Absatz nach; steht er am Ende, fällt er weg – und damit auch
/// aus dem Verlauf, sonst gewöhnt sich das Modell die Floskel im Gespräch an. Besteht die ganze Antwort nur
/// aus so einem Absatz, ist es eine echte Rückfrage und bleibt. Dasselbe gilt für Abschluss-Rückfragen
/// („Passt das so?“) – die fallen aber nur weg, wenn der Absatz wirklich eine Frage ist. Code-Blöcke bleiben unberührt.
/// Eine Trennlinie ("---") ganz am Ende fällt weg – sonst steht nach der Floskel noch etwas, sie bliebe stehen, und
/// das Modell hängt die Linie im Gespräch bald an jede Antwort (Selbsttest 51). Folgt einer Floskel nur noch die
/// Quelle ("Quelle: …"), fällt die Floskel weg, die Quelle bleibt.
/// Gelernte Floskeln: Beginnt der Schluss wie der Schluss einer früheren Antwort und ist sein erster Satz derselbe, ist
/// es eine Angewohnheit, kein Inhalt – Selbsttest 53 hängte "Und schwarz getrunken? …" an jede Antwort.
/// </summary>
internal sealed partial class ClosingFilter
{
    internal static readonly string[] Phrases =
    [
        "Möchtest du", "Willst du", "Soll ich", "Brauchst du", "Hast du noch",
        "Wenn du noch", "Wenn du mir", "Wenn du magst", "Wenn du willst", "Wenn du möchtest",
        "Falls du noch", "Falls du mehr", "Falls du weitere",
        "Sag Bescheid", "Sag einfach Bescheid", "Sag mir Bescheid", "Lass mich wissen",
        "Gibt es noch", "Kann ich dir noch", "Was noch", "Oder hast du",
    ];

    /// <summary>
    /// Abschluss-Rückfragen, die nur abfragen, ob die Antwort gefällt („Passt das zu deinem Herbst?“,
    /// „Was dich am meisten interessiert?“). Fallen am Ende nur weg, wenn wirklich eine Frage darin steht.
    /// Echte Gegenfragen im Gespräch („Und bei dir?“) bleiben.
    /// </summary>
    internal static readonly string[] CheckQuestions =
    [
        "Passt das", "Passt dir", "Klingt das", "Wie klingt das", "Hilft dir das", "Hilft das", "Reicht das", "Genügt das",
        "Was dich", "Was interessiert dich", "Interessiert dich", "Welche davon", "Welcher davon", "Welches davon",
        "Was davon", "Worauf hast du", "Wofür interessierst du",
        "Noch etwas", "Noch eine Frage", "Noch Fragen", "Gibt es etwas", "Hast du spezielle", "Hast du besondere", "Hast du bestimmte",
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
    private bool _ruleOnly;                         // mitten im Absatz: nur prüfen, ob die Zeile eine Trennlinie wird
    private bool _heldLearned;                      // zurückgehalten, weil der Absatz wie ein früherer Schluss beginnt
    private readonly List<string> _learned;         // erste Sätze früherer Schlussabsätze (klein, nur Wörter)

    /// <param name="earlierClosings">Die Schlussabsätze früherer Antworten (siehe <see cref="ClosingParagraph"/>).</param>
    public ClosingFilter(IEnumerable<string>? earlierClosings = null)
    {
        _learned = (earlierClosings ?? []).Select(FirstSentence).Where(s => s.Split(' ').Length >= 3).Distinct().ToList();
    }

    /// <summary>
    /// Der Schlussabsatz einer Antwort – ein kurzer Absatz ohne Code, Liste oder Tabelle hinter mindestens einem
    /// anderen. Null, wenn es keinen gibt.
    /// </summary>
    internal static string? ClosingParagraph(string answer)
    {
        var text = WithoutTrailingRules(answer.ReplaceLineEndings("\n").TrimEnd()).TrimEnd();
        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        if (paragraphs.Length < 2)
            return null;
        var last = paragraphs[^1].Trim();
        return last.Length <= 240 && !last.Contains('\n') && !last.StartsWith("```", StringComparison.Ordinal)
            && !last.StartsWith('|') && !last.StartsWith("- ", StringComparison.Ordinal) ? last : null;
    }

    /// <summary>Der erste Satz ohne Farb-Tags und Satzzeichen, klein: "Und schwarz getrunken? Ich …" → "und schwarz getrunken".</summary>
    internal static string FirstSentence(string paragraph)
    {
        var text = Max.Ui.ColorTags.Strip(paragraph);
        var end = text.IndexOfAny(['.', '?', '!', ':']);
        return Words(end > 0 ? text[..end] : text);
    }

    private static string Words(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
        }
        return builder.ToString().Trim();
    }

    /// <summary>Beginnt der Absatz wie der Schluss einer früheren Antwort (die ersten drei Wörter)? null = noch zu kurz.</summary>
    private bool? StartsLikeLearned(string line, bool complete)
    {
        var text = line.TrimStart();
        if (text.StartsWith('{') && text.IndexOf('}') < 0)
            return complete ? false : null;                 // erst das Farb-Tag abwarten
        var start = Words(Max.Ui.ColorTags.Strip(text));
        var undecided = false;
        foreach (var learned in _learned)
        {
            var prefix = string.Join(' ', learned.Split(' ').Take(3));
            if (start.StartsWith(prefix, StringComparison.Ordinal) && (start.Length == prefix.Length || start[prefix.Length] == ' '))
            {
                _heldLearned = true;
                return true;
            }
            if (!complete && prefix.StartsWith(start, StringComparison.Ordinal))
                undecided = true;
        }
        return undecided ? null : false;
    }

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
            if (!_ruleOnly && StartsWithPhrase(line, complete: true) == true || IsRule(line))
                _held.Append(line);
            else
                Emit(line, output);
        }
        if (_held.Length > 0)
        {
            var held = WithoutTrailingRules(_held.ToString());
            if (held.Trim().Length == 0)
                LlmEngine.Log("Trennlinie am Ende weggelassen.");
            else if (_heldLearned)
            {
                if (_anyShown && _learned.Contains(FirstSentence(held)))
                    LlmEngine.Log($"Wiederholten Schluss weggelassen: {held.Trim().ReplaceLineEndings(" ")}");
                else
                    output.Append(held);
            }
            else if (!_anyShown || StartsWithPhrase(held, complete: true, Phrases) != true && !held.Contains('?'))
                output.Append(held);
            else
                LlmEngine.Log($"Floskel am Ende weggelassen: {held.Trim().ReplaceLineEndings(" ")}");
            _held.Clear();
        }
        _holding = false;
        _heldLearned = false;
        return output.ToString();
    }

    private void Add(char c, StringBuilder output)
    {
        if (_deciding)
        {
            _line.Append(c);
            var line = _line.ToString();
            switch (Decide(line, complete: c == '\n'))
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
                    if (IsSource(line))
                        DropClosing(output);
                    else
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
            // In einem zurückgehaltenen Absatz zählt jede Zeile; sonst nur Zeilen am Absatz-Anfang – und jede Zeile,
            // die eine Trennlinie werden könnte.
            if ((_paragraphStart || _holding || c is '-' or '*' or '_') && !char.IsWhiteSpace(c))
            {
                _ruleOnly = !_paragraphStart && !_holding;
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

    /// <summary>Nach einer Floskel kommt nur noch die Quelle: Floskel weg, Trennlinien davor bleiben.</summary>
    private void DropClosing(StringBuilder output)
    {
        var held = _held.ToString();
        if (!_anyShown || StartsWithPhrase(held, complete: true) != true)
        {
            Release(output);
            return;
        }
        var lines = held.Split('\n');
        var rule = Array.FindIndex(lines, IsRule);
        LlmEngine.Log($"Floskel vor der Quelle weggelassen: {(rule < 0 ? held : string.Join(' ', lines[..rule])).Trim()}");
        _held.Clear();
        _holding = false;
        if (rule >= 0)
            Emit(string.Join('\n', lines[rule..]), output);
    }

    /// <summary>Eine Zeile, die nur eine Trennlinie ist: "---", "***", "___", "- - -".</summary>
    internal static bool IsRule(string line) => RuleRegex().IsMatch(line);

    /// <summary>"Quelle: …", "Quellen: …", "(Quelle: …)" – die Herkunft einer Antwort.</summary>
    private static bool IsSource(string line) => SourceRegex().IsMatch(line);

    /// <summary>Linien (und Leerzeilen) am Ende weg.</summary>
    internal static string WithoutTrailingRules(string text)
    {
        var lines = text.Split('\n').ToList();
        while (lines.Count > 0 && (lines[^1].Trim().Length == 0 || IsRule(lines[^1])))
            lines.RemoveAt(lines.Count - 1);
        return lines.Count == 0 ? "" : string.Join('\n', lines) + (text.EndsWith('\n') ? "\n" : "");
    }

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^\s*[(*_]*(Quelle|Quellen)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SourceRegex();

    private void Release(StringBuilder output)
    {
        _heldLearned = false;
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

    /// <summary>
    /// Entfernt eine Aufzählung ganz am Ende, wenn direkt davor eine Frage steht – die Frage bleibt.
    /// Nur Listen aus kurzen Punkten (Antwortmöglichkeiten), keine inhaltlichen Aufzählungen.
    /// </summary>
    internal static string WithoutTrailingOptions(string text)
    {
        var lines = text.TrimEnd().Split('\n');
        var first = lines.Length;
        while (first > 0 && IsOption(lines[first - 1]))
            first--;
        if (first == lines.Length || lines.Length - first < 2 || first == 0 || !lines[first - 1].TrimEnd().EndsWith('?'))
            return text;
        return string.Join('\n', lines[..first]);
    }

    private static bool IsOption(string line)
    {
        var text = line.Trim();
        return text.Length is > 2 and <= 60 && (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal) || text.StartsWith("• ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Gehört die Zeile zur Floskel? Hinter einer zurückgehaltenen Floskel zählen auch Aufzählungspunkte dazu –
    /// "Möchtest du mehr? / - Theorie / - Praxis" ist ein Angebot mit Antwortmöglichkeiten.
    /// </summary>
    private bool? Decide(string line, bool complete)
    {
        // Nur Striche, Sternchen, Unterstriche: Wird das eine Trennlinie? Dann zurückhalten, bis klar ist, ob noch etwas kommt.
        var trimmed = line.Trim();
        if (trimmed.Length > 0 && trimmed.All(c => c is '-' or '*' or '_' or ' '))
        {
            if (!complete)
                return null;
            if (IsRule(trimmed))
                return true;
        }
        if (_ruleOnly)
            return false;
        if (_holding)
        {
            // Hinter einer Floskel: Wird das die Quelle ("Quelle: …")? Dann fällt die Floskel weg – erst abwarten.
            var start = trimmed.TrimStart('(', '*', '_');
            if (!complete && start.Length < 6 && "quelle".StartsWith(start, StringComparison.OrdinalIgnoreCase))
                return null;
            var text = line.TrimStart();
            if (text.Length == 1 && text[0] is '-' or '*' or '•' && !complete)
                return null;
            if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal) || text.StartsWith("• ", StringComparison.Ordinal))
                return true;
        }
        var phrase = StartsWithPhrase(line, complete);
        if (phrase != false || _holding || _learned.Count == 0)
            return phrase;
        return StartsLikeLearned(line, complete);
    }

    /// <summary>true = Floskel, false = sicher keine, null = noch zu kurz, um es zu sagen.</summary>
    internal static bool? StartsWithPhrase(string line, bool complete) => StartsWithPhrase(line, complete, [.. Phrases, .. CheckQuestions]);

    private static bool? StartsWithPhrase(string line, bool complete, string[] phrases)
    {
        var text = line.TrimStart().TrimStart('*', '_', '>', ' ');
        // Farb-Tags davor überspringen ({cyan}, {verlauf:…}, [rot]) – die Floskel dahinter zählt.
        while (text.Length > 0 && text[0] is '{' or '[')
        {
            var close = text.IndexOf(text[0] == '{' ? '}' : ']');
            if (close < 0)
                return complete || text.Length > 40 ? false : null;
            text = text[(close + 1)..].TrimStart('*', '_', ' ');
        }
        var undecided = false;
        foreach (var phrase in phrases)
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
