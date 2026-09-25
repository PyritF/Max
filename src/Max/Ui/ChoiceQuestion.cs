using System.Text.RegularExpressions;

namespace Max.Ui;

/// <summary>
/// Eine Rückfrage von Max mit vorgegebenen Antworten, geschrieben als <c>```frage</c>-Block:
/// <code>
/// Frage: Für welche Sprache?
/// - C#
/// - Python
/// </code>
/// Max zeigt die Frage im Text; danach erscheint ein Auswahlmenü mit den Antworten und einer Zeile für eigenen Text.
/// </summary>
internal sealed partial record ChoiceQuestion(string Question, IReadOnlyList<string> Options)
{
    /// <summary>Namen, unter denen das Modell den Block schreiben darf.</summary>
    public static readonly HashSet<string> BlockNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "frage", "rückfrage", "rueckfrage", "auswahl", "question", "choice",
    };

    public const int MaxOptions = 9;

    /// <summary>Liest den Block-Inhalt. Null, wenn keine Frage darin steckt.</summary>
    public static ChoiceQuestion? TryParse(string body)
    {
        string? question = null;
        var options = new List<string>();

        foreach (var raw in body.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (KeyRegex().Match(line) is { Success: true } key)
            {
                var name = key.Groups[1].Value.ToLowerInvariant();
                var value = key.Groups[2].Value.Trim();
                if (name is "frage" or "question" or "titel")
                    question = value;
                else if (Clean(value) is { Length: > 0 } answer)  // "Antwort: Ja" – als Option werten
                    options.Add(answer);
                continue;
            }

            if (OptionRegex().Match(line) is { Success: true } option)
            {
                var text = Clean(option.Groups[1].Value);
                if (text.Length > 0)
                    options.Add(text);
                continue;
            }

            question ??= line;
        }

        options = options.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxOptions).ToList();
        if (string.IsNullOrWhiteSpace(question) && options.Count == 0)
            return null;
        return new ChoiceQuestion(question ?? "", options);
    }

    /// <summary>Optionen sind schlichter Text: Markdown-Zeichen und Farb-Tags fallen weg.</summary>
    private static string Clean(string text) =>
        TagRegex().Replace(text, "").Replace("**", "").Replace("`", "").Trim();

    [GeneratedRegex(@"\{/?[\p{L}]+(?:[:= ][^}]*)?\}")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^(Frage|Question|Titel|Antworten|Optionen|Antwort|Option)\s*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"^(?:[-*•]|\d{1,2}[.)]|\[\s?\])\s+(.*)$")]
    private static partial Regex OptionRegex();
}
