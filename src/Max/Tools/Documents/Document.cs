namespace Max.Tools.Documents;

/// <summary>
/// Ein Stück eines Dokuments, durch das Max blättern kann: eine Seite, eine Folie, ein Tabellenblatt – bei einer
/// Textdatei die ganze Datei (dann zählen die Zeilen).
/// </summary>
/// <param name="number">So zählt der Nutzer: Seite 7, Folie 3, Blatt 2.</param>
/// <param name="label">So steht es in der Ausgabe: "Seite 7", "Folie 3", "Blatt „Umsatz“" – leer bei einer Textdatei.</param>
/// <param name="lineNumbers">Nummer jeder Zeile (Textdatei, Tabellenzeilen) – sonst null.</param>
internal sealed class DocumentPart(int number, string label, IReadOnlyList<string> lines, IReadOnlyList<int>? lineNumbers = null)
{
    public int Number { get; } = number;
    public string Label { get; } = label;
    public IReadOnlyList<string> Lines { get; private set; } = lines;
    public IReadOnlyList<int>? LineNumbers { get; } = lineNumbers;

    /// <summary>Name eines Tabellenblatts ("Umsatz") – für "Blatt Umsatz, Zeile 120".</summary>
    public string? Name { get; init; }

    /// <summary>Nur ein Bild (eingescannt) – den Text gibt es erst über den Bild-Zusatz.</summary>
    public bool Scanned { get; private set; }

    public int Length => Lines.Sum(l => l.Length + 1);

    public string Text => string.Join('\n', Lines);

    public static DocumentPart ScannedPage(int number) => new(number, $"Seite {number}", []) { Scanned = true };

    /// <summary>Der abgelesene Text einer eingescannten Seite – einmal gelesen, bleibt er.</summary>
    public void SetScannedText(string text)
    {
        Lines = DocumentReader.SplitLines(text);
        Scanned = false;
    }
}

/// <summary>Ein gelesenes Dokument: was es ist, wie man darin blättert, und seine Stücke.</summary>
/// <param name="kind">"PDF", "Word-Dokument", "Excel-Tabelle", "PowerPoint", "Textdatei", "Webseite".</param>
/// <param name="unit">Womit man darin blättert: "Seite", "Folie", "Blatt", "Teil" – bei Textdateien "Zeile".</param>
/// <param name="total">So viele davon gibt es (bei Textdateien Zeilen).</param>
internal sealed class Document(string kind, string unit, int total, IReadOnlyList<DocumentPart> parts)
{
    public string Kind { get; } = kind;
    public string Unit { get; } = unit;
    public int Total { get; } = total;
    public IReadOnlyList<DocumentPart> Parts { get; } = parts;

    /// <summary>Hinweis für die Kopfzeile, z. B. "nur die ersten 400 Seiten gelesen".</summary>
    public string? Note { get; init; }

    /// <summary>Liest eine eingescannte Seite über den Bild-Zusatz – null, wenn es ihn (noch) nicht gibt.</summary>
    public Func<DocumentPart, CancellationToken, Task<string?>>? ReadScan { get; init; }

    /// <summary>Eine Textdatei (oder Ähnliches): ein einziges Stück, gezählt wird in Zeilen.</summary>
    public bool ByLines => Unit == "Zeile";

    /// <summary>"23 Seiten", "1 Folie", "4 Blätter".</summary>
    public static string Count(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {Plural(unit)}";

    public static string Plural(string unit) => unit switch
    {
        "Blatt" => "Blätter",
        "Teil" => "Teile",
        _ => unit + "n",
    };
}

/// <summary>Ein Dokument ließ sich nicht lesen – mit einem Satz, der das erklärt.</summary>
internal sealed class DocumentException(string message) : Exception(message);
