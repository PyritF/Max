using Spectre.Console;

namespace Max.Ui;

/// <summary>
/// Zentrale Farbpalette von Max. Alle Farben der Oberfläche kommen von hier,
/// damit das Aussehen an einer einzigen Stelle angepasst werden kann.
/// </summary>
internal static class Theme
{
    /// <summary>Akzentfarbe: Max' Symbol, Rahmen, Überschriften.</summary>
    public static readonly Color Accent = new(255, 70, 50);

    /// <summary>Start- und Endfarbe des Farbverlaufs im Logo (oben → unten).</summary>
    public static readonly Color GradientStart = new(255, 40, 60);
    public static readonly Color GradientEnd = new(255, 150, 40);

    /// <summary>Normaler Text.</summary>
    public static readonly Color Text = new(220, 220, 220);

    /// <summary>Abgeschlossenes, in den Hintergrund Tretendes (erledigte Schritte).</summary>
    public static readonly Color Dim = new(154, 154, 154);

    /// <summary>Dezente Farbe für Nebensächliches (Beschriftungen, Hinweise).</summary>
    public static readonly Color Muted = new(128, 128, 128);

    /// <summary>Platzhaltertext, z. B. in der leeren Eingabezeile.</summary>
    public static readonly Color Placeholder = new(90, 90, 90);

    /// <summary>Trennlinien.</summary>
    public static readonly Color Border = new(58, 58, 58);

    /// <summary>Trennlinien innerhalb des roten Kastens – ein abgedunkeltes Rot.</summary>
    public static readonly Color AccentDivider = new(58, 36, 34);

    /// <summary>Leerer Teil eines Fortschrittsbalkens.</summary>
    public static readonly Color Track = new(51, 51, 51);

    /// <summary>Statusanzeige "alles in Ordnung", Häkchen.</summary>
    public static readonly Color Success = new(80, 220, 120);

    /// <summary>Das Symbol, das vor jeder Äußerung von Max steht.</summary>
    public const string Symbol = "◆";

    /// <summary>Mischt zwei Farben. t = 0 → a, t = 1 → b.</summary>
    public static Color Blend(Color a, Color b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Kurzform für Markup: <c>Theme.Tag(Theme.Accent)</c> → "rgb(255,70,50)".</summary>
    public static string Tag(Color color) => color.ToMarkup();
}
