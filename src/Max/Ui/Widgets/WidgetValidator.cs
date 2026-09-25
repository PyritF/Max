namespace Max.Ui.Widgets;

/// <summary>
/// Prüft, ob sich ein Element-Block zeichnen lässt – mit genau der Logik, die ihn später zeichnet.
/// Das Backend nutzt das, um kaputte Blöcke unsichtbar neu erzeugen zu lassen.
/// </summary>
internal static class WidgetValidator
{
    /// <summary>Ist das ein Element (inkl. Rückfrage) statt normalem Code?</summary>
    public static bool IsElement(string name) =>
        WidgetRegistry.IsWidget(name) || ChoiceQuestion.BlockNames.Contains(name);

    public static bool IsValid(string name, string body)
    {
        if (ChoiceQuestion.BlockNames.Contains(name))
            return ChoiceQuestion.TryParse(body) is { Options.Count: > 0 };
        return WidgetRegistry.TryRender(name, body, 80) is not null;
    }
}
