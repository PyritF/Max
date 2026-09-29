using System.IO.Compression;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Max.Tools;

/// <summary>
/// Text aus Dokumenten, die keine Textdateien sind: PDF (PdfPig) und Word (.docx – ein Zip mit XML darin).
/// Nur lesen, nichts ausführen: keine Makros, keine eingebetteten Objekte, keine Verweise ins Netz.
/// </summary>
internal static class DocumentText
{
    /// <summary>Größer liest Max ein Dokument nicht (der Text wird ohnehin gekürzt).</summary>
    internal const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>So viele PDF-Seiten höchstens – mehr passt ohnehin nicht ins Ergebnis.</summary>
    internal const int MaxPages = 60;

    public static bool IsDocument(string path) => Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".docx";

    /// <summary>Der Text des Dokuments – oder ein Satz, warum es nicht geht.</summary>
    public static string Read(string path, CancellationToken ct)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".pdf" => Pdf(path, ct),
                ".docx" => Word(path, ct),
                _ => $"{Path.GetFileName(path)} ist kein Dokument, das ich lesen kann.",
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"{Path.GetFileName(path)} ließ sich nicht lesen ({e.Message}).";
        }
    }

    private static string Pdf(string path, CancellationToken ct)
    {
        using var document = PdfDocument.Open(path);
        var text = new StringBuilder();
        var pages = document.NumberOfPages;
        for (var number = 1; number <= Math.Min(pages, MaxPages); number++)
        {
            ct.ThrowIfCancellationRequested();
            var page = document.GetPage(number);
            var content = ContentOrderTextExtractor.GetText(page).Trim();
            if (content.Length > 0)
                text.Append("--- Seite ").Append(number).Append(" ---\n").Append(content).Append("\n\n");
            if (text.Length > ToolBox.MaxResultChars * 2)
                break;
        }
        if (text.Length == 0)
            return $"{Path.GetFileName(path)} hat {pages} Seite(n), aber keinen lesbaren Text – vermutlich eingescannt (nur Bilder).";
        return $"PDF mit {pages} Seite(n):\n{text.ToString().TrimEnd()}";
    }

    /// <summary>Absätze aus word/document.xml; Tabellenzellen mit " | " getrennt, Zeilen je Tabellenzeile.</summary>
    private static string Word(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("kein Word-Dokument");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });

        const string ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var text = new StringBuilder();
        var cellOpen = false;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NamespaceURI != ns)
                continue;
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        text.Append(reader.ReadElementContentAsString());
                        break;
                    case "tab":
                        text.Append('\t');
                        break;
                    case "br" or "cr":
                        text.Append('\n');
                        break;
                    case "tc":
                        if (cellOpen)
                            TrimEnd(text).Append(" | ");
                        cellOpen = true;
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "p" when !cellOpen:
                        text.Append('\n');
                        break;
                    case "p":
                        text.Append(' ');
                        break;
                    case "tr":
                        TrimEnd(text).Append('\n');
                        cellOpen = false;
                        break;
                }
            }
        }
        var result = text.ToString().Trim();
        static StringBuilder TrimEnd(StringBuilder b)
        {
            while (b.Length > 0 && b[^1] == ' ')
                b.Length--;
            return b;
        }
        return result.Length == 0 ? $"{Path.GetFileName(path)} enthält keinen Text." : $"Word-Dokument:\n{result}";
    }
}
