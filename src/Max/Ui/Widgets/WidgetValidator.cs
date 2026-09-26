namespace Max.Ui.Widgets;

/// <summary>
/// Prüft, ob sich ein Element-Block zeichnen lässt – mit genau der Logik, die ihn später zeichnet.
/// Das Backend nutzt das, um kaputte Blöcke unsichtbar neu erzeugen zu lassen.
/// </summary>
internal static partial class WidgetValidator
{
    /// <summary>Ist das ein Element (inkl. Rückfrage) statt normalem Code?</summary>
    public static bool IsElement(string name) =>
        WidgetRegistry.IsWidget(name) || ChoiceQuestion.BlockNames.Contains(name);

    public static bool IsValid(string name, string body)
    {
        if (ChoiceQuestion.BlockNames.Contains(name))
            return ChoiceQuestion.TryParse(body) is { Options.Count: > 1 } question && IsRealChoice(question);
        if (NumericWidgets.Contains(name))
        {
            var labels = new WidgetBody(body).Numbers().Select(p => p.Label).ToList();
            // Doppelte Namen ("Stadt: 1. Wien", "Stadt: 2. Linz") heißen: Das war eine Tabelle, kein Diagramm.
            if (labels.Any(l => !IsShortLabel(l)) || labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() < labels.Count)
                return false;
            // Ein einzelner Balken vergleicht nichts.
            if (name is "balken" or "anteile" && labels.Count < 2)
                return false;
        }
        return WidgetRegistry.TryRender(name, body, 80) is not null;
    }

    /// <summary>
    /// Eine echte Rückfrage: eine Frage mit "?" und Antworten, zwischen denen man wählt.
    /// "Möchtest du mehr? – Ja / Nein" ist nur ein Angebot und gehört nicht in ein Menü.
    /// </summary>
    internal static bool IsRealChoice(ChoiceQuestion question) =>
        question.Question.TrimEnd().EndsWith('?')
        && !question.Options.Any(o => YesNoRegex().IsMatch(o));

    [System.Text.RegularExpressions.GeneratedRegex(@"^(ja|nein|nö|jep|nee|klar|gerne?|lieber nicht|yes|no)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex YesNoRegex();

    private static readonly HashSet<string> NumericWidgets = new(StringComparer.OrdinalIgnoreCase) { "balken", "anteile", "kurve", "fortschritt" };

    /// <summary>
    /// Ein Diagramm-Name ist kurz und einspaltig. "Salzbergwerk     Führung     €10" ist eine
    /// Tabellenzeile im falschen Element – die gehört neu erzeugt.
    /// </summary>
    internal static bool IsShortLabel(string label) =>
        label.Length <= 30 && !label.Contains('\t') && !label.Contains("  ", StringComparison.Ordinal);
}
