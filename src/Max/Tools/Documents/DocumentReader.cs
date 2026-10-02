using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Max.Llm;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Max.Tools.Documents;

/// <summary>
/// Liest Dateien in ein <see cref="Document"/>: Textdateien, PDF (PdfPig), Word, Excel und PowerPoint (Zip mit XML
/// darin). Nur lesen, nichts ausführen: keine Makros, keine eingebetteten Objekte, keine Verweise ins Netz.
/// Eingescannte PDF-Seiten liest später der Bild-Zusatz – erst wenn eine davon gezeigt werden soll.
/// </summary>
internal static class DocumentReader
{
    /// <summary>Größer liest Max ein Dokument (PDF, Office) nicht.</summary>
    internal const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>Von Textdateien höchstens so viel – mehr als genug zum Suchen.</summary>
    internal const int MaxTextBytes = 16 * 1024 * 1024;

    /// <summary>So viele PDF-Seiten höchstens.</summary>
    internal const int MaxPages = 400;

    /// <summary>So viel Text höchstens (alle Seiten zusammen) – darüber wird es zum Suchen nur langsam.</summary>
    internal const int MaxChars = 4_000_000;

    /// <summary>Tabellen: so viele Zeilen und Spalten höchstens.</summary>
    internal const int MaxRows = 50_000;
    internal const int MaxColumns = 100;

    /// <summary>Word ohne Seitenangaben und Webseiten: in Teile dieser Größe zerlegt.</summary>
    internal const int TeilChars = 3000;

    private static readonly XmlReaderSettings Safe = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    public static bool IsOffice(string path) => Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".pptm";

    public static bool IsDocument(string path) => IsOffice(path) || Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Liest die Datei. Geht das nicht, kommt eine <see cref="DocumentException"/> mit einem Satz dazu.</summary>
    /// <param name="vision">Bildverständnis für eingescannte PDF-Seiten – null, wenn es keins gibt.</param>
    /// <param name="maxPages">Von PDFs höchstens so viele Seiten (zum Durchsuchen vieler Dateien weniger).</param>
    public static Document Load(string path, Func<IVision?>? vision, CancellationToken ct, int maxPages = MaxPages)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".doc" or ".xls" or ".ppt")
            throw new DocumentException($"{name} ist im alten Office-Format – das kann ich nicht lesen. Als .docx, .xlsx, .pptx oder PDF gespeichert geht es.");
        var length = new FileInfo(path).Length;
        try
        {
            if (IsDocument(path))
            {
                if (length > MaxBytes)
                    throw new DocumentException($"{name} ist zu groß ({ListFolderTool.Size(length)}, höchstens {MaxBytes / 1024 / 1024} MB).");
                return extension switch
                {
                    ".pdf" => Pdf(path, vision, maxPages, ct),
                    ".docx" or ".docm" => Word(path, ct),
                    ".xlsx" or ".xlsm" => Excel(path, ct),
                    _ => PowerPoint(path, ct),
                };
            }
            return Text(path, length);
        }
        catch (Exception e) when (e is not (OperationCanceledException or DocumentException or UnauthorizedAccessException))
        {
            if (e is PdfDocumentEncryptedException)
                throw new DocumentException($"{name} ist mit einem Passwort geschützt – das kann ich nicht öffnen.");
            throw new DocumentException($"{name} ließ sich nicht lesen ({e.Message}).");
        }
    }

    // ── Textdateien ────────────────────────────────────────────────────────────────────────────────

    private static Document Text(string path, long length)
    {
        var buffer = new byte[(int)Math.Min(length, MaxTextBytes)];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var read = 0;
            while (read < buffer.Length && stream.Read(buffer, read, buffer.Length - read) is var n and > 0)
                read += n;
            if (read < buffer.Length)
                Array.Resize(ref buffer, read);
        }
        if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(buffer.Length, 8192)) >= 0)
            throw new DocumentException($"{Path.GetFileName(path)} ist keine Textdatei ({ListFolderTool.Size(length)}).");

        var lines = SplitLines(Decode(buffer));
        return new Document("Textdatei", "Zeile", lines.Count, [new DocumentPart(1, "", lines, Enumerable.Range(1, lines.Count).ToArray())])
        {
            Note = length > MaxTextBytes ? $"nur die ersten {MaxTextBytes / 1024 / 1024} MB gelesen" : null,
        };
    }

    /// <summary>UTF-8, sonst (alte Windows-Dateien) Windows-1252.</summary>
    internal static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            // Mitten in einem Zeichen abgeschnitten? Dann ist es trotzdem UTF-8.
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Count(c => c == '�') <= 1)
                return text.TrimStart('﻿');
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    internal static List<string> SplitLines(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // ── PDF ────────────────────────────────────────────────────────────────────────────────────────

    private static Document Pdf(string path, Func<IVision?>? vision, int maxPages, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(path);
        var count = pdf.NumberOfPages;
        var parts = new List<DocumentPart>();
        var chars = 0;
        for (var number = 1; number <= Math.Min(count, maxPages) && chars < MaxChars; number++)
        {
            ct.ThrowIfCancellationRequested();
            var page = pdf.GetPage(number);
            string text;
            try
            {
                text = ContentOrderTextExtractor.GetText(page).Trim();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LlmEngine.Log($"PDF-Seite {number} nicht lesbar: {e.Message}");
                text = "";
            }
            // Fast kein Text, aber ein Bild: eingescannt (eine Seitenzahl allein ist noch kein Text).
            if (text.Length < 20 && page.NumberOfImages > 0)
            {
                parts.Add(DocumentPart.ScannedPage(number));
                continue;
            }
            parts.Add(new DocumentPart(number, $"Seite {number}", SplitLines(text)));
            chars += text.Length;
        }
        var read = parts.Count;
        return new Document("PDF", "Seite", count, parts)
        {
            Note = read < count ? $"nur die ersten {read} Seiten gelesen" : null,
            ReadScan = vision is null ? null : (part, token) => PdfScan.ReadAsync(path, part.Number, vision(), token),
        };
    }

    // ── Word ───────────────────────────────────────────────────────────────────────────────────────

    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// Absätze aus word/document.xml, Tabellenzeilen als "Zelle | Zelle". Seiten gibt es, wenn Word beim Speichern
    /// die Seitenumbrüche vermerkt hat (lastRenderedPageBreak) – sonst Teile.
    /// </summary>
    private static Document Word(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new DocumentException($"{Path.GetFileName(path)} ist kein Word-Dokument.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, Safe);

        var pages = new List<List<string>> { new() };
        var paragraph = new StringBuilder();
        var cell = new StringBuilder();
        var row = new List<string>();
        var cellDepth = 0;
        var inText = false;
        var breaks = 0;

        void EndParagraph()
        {
            var text = paragraph.ToString().Trim();
            paragraph.Clear();
            if (text.Length == 0)
                return;
            if (cellDepth > 0)
                cell.Append(cell.Length > 0 ? " " : "").Append(text.Replace('\n', ' '));
            else
                pages[^1].AddRange(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        }

        void NewPage()
        {
            if (cellDepth > 0)
                return;             // mitten in einer Tabelle: nach der Zeile weiter auf der alten Seite
            EndParagraph();
            pages.Add([]);
            breaks++;
        }

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                if (inText)
                    paragraph.Append(reader.Value);
                continue;
            }
            if (reader.NamespaceURI != WordNs)
                continue;
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        inText = !reader.IsEmptyElement;
                        break;
                    case "tab":
                        paragraph.Append('\t');
                        break;
                    case "br" when reader.GetAttribute("type", WordNs) == "page":
                    case "lastRenderedPageBreak":
                        NewPage();
                        break;
                    case "br" or "cr":
                        paragraph.Append('\n');
                        break;
                    case "tc":
                        cellDepth++;
                        if (cellDepth == 1)
                            cell.Clear();
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        inText = false;
                        break;
                    case "p":
                        EndParagraph();
                        break;
                    case "tc":
                        cellDepth--;
                        if (cellDepth == 0)
                            row.Add(cell.ToString().Trim());
                        break;
                    case "tr" when cellDepth == 0:
                        if (row.Any(c => c.Length > 0))
                            pages[^1].Add(string.Join(" | ", row));
                        row.Clear();
                        break;
                }
            }
        }
        EndParagraph();

        var lines = pages.SelectMany(p => p).ToList();
        if (lines.Count == 0)
            throw new DocumentException($"{Path.GetFileName(path)} enthält keinen Text.");
        if (breaks == 0)
            return Chunked("Word-Dokument", lines);
        var parts = pages.Select((p, i) => new DocumentPart(i + 1, $"Seite {i + 1}", p)).ToList();
        return new Document("Word-Dokument", "Seite", parts.Count, parts);
    }

    /// <summary>Ohne Seiten: der Text in Teile von etwa <see cref="TeilChars"/> Zeichen, an Zeilengrenzen.</summary>
    internal static Document Chunked(string kind, IReadOnlyList<string> lines)
    {
        var parts = new List<DocumentPart>();
        var current = new List<string>();
        var size = 0;
        foreach (var line in lines.SelectMany(l => l.Length > TeilChars ? Slice(l, TeilChars) : [l]))
        {
            if (size + line.Length > TeilChars && current.Count > 0)
            {
                parts.Add(new DocumentPart(parts.Count + 1, $"Teil {parts.Count + 1}", current));
                current = [];
                size = 0;
            }
            current.Add(line);
            size += line.Length + 1;
        }
        if (current.Count > 0)
            parts.Add(new DocumentPart(parts.Count + 1, $"Teil {parts.Count + 1}", current));
        return new Document(kind, "Teil", parts.Count, parts);
    }

    /// <summary>Eine überlange Zeile in Stücke – möglichst an Satz- oder Wortgrenzen.</summary>
    internal static IEnumerable<string> Slice(string line, int size)
    {
        while (line.Length > size)
        {
            var cut = line.LastIndexOfAny(['.', '!', '?'], size - 1, size / 2);
            if (cut < 0)
                cut = line.LastIndexOf(' ', size - 1, size / 2);
            cut = cut < 0 ? size : cut + 1;
            yield return line[..cut].Trim();
            line = line[cut..];
        }
        if (line.Trim().Length > 0)
            yield return line.Trim();
    }

    // ── Excel ──────────────────────────────────────────────────────────────────────────────────────

    private const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Jedes Tabellenblatt ein Stück; jede Zeile "Zelle | Zelle | …", die Zeilennummern wie in Excel.</summary>
    private static Document Excel(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var workbook = LoadXml(zip, "xl/workbook.xml") ?? throw new DocumentException($"{Path.GetFileName(path)} ist keine Excel-Datei.");
        XNamespace s = SheetNs, r = RelNs;
        var date1904 = workbook.Descendants(s + "workbookPr").Attributes("date1904").Any(a => a.Value is "1" or "true");
        var targets = Relationships(zip, "xl/_rels/workbook.xml.rels", "xl/");
        var strings = SharedStrings(zip);
        var formats = CellFormats(zip);

        var parts = new List<DocumentPart>();
        var rows = 0;
        foreach (var sheet in workbook.Descendants(s + "sheet"))
        {
            ct.ThrowIfCancellationRequested();
            var name = (string?)sheet.Attribute("name") ?? $"Blatt {parts.Count + 1}";
            var id = (string?)sheet.Attribute(r + "id");
            if (id is null || !targets.TryGetValue(id, out var target) || zip.GetEntry(target) is not { } entry)
                continue;
            var (lines, numbers) = SheetRows(entry, strings, formats, date1904, MaxRows - rows, ct);
            rows += lines.Count;
            var hidden = (string?)sheet.Attribute("state") is "hidden" or "veryHidden" ? " (ausgeblendet)" : "";
            parts.Add(new DocumentPart(parts.Count + 1, $"Blatt „{name}“{hidden}", lines, numbers) { Name = name });
        }
        if (parts.Count == 0)
            throw new DocumentException($"{Path.GetFileName(path)} enthält keine Tabellenblätter.");
        return new Document("Excel-Tabelle", "Blatt", parts.Count, parts)
        {
            Note = rows >= MaxRows ? $"nur die ersten {MaxRows} Zeilen gelesen" : null,
        };
    }

    private static (List<string> Lines, List<int> Numbers) SheetRows(
        ZipArchiveEntry entry, IReadOnlyList<string> strings, IReadOnlyList<CellFormat> formats, bool date1904, int maxRows, CancellationToken ct)
    {
        var lines = new List<string>();
        var numbers = new List<int>();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, Safe);
        var cells = new List<string>();
        var rowNumber = 0;
        while (reader.Read() && lines.Count < maxRows)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != SheetNs)
                continue;
            if (reader.LocalName == "row")
            {
                ct.ThrowIfCancellationRequested();
                rowNumber = int.TryParse(reader.GetAttribute("r"), out var r) ? r : rowNumber + 1;
                cells.Clear();
                if (reader.IsEmptyElement)
                    continue;
                using var row = reader.ReadSubtree();
                while (row.Read())
                {
                    if (row.NodeType != XmlNodeType.Element || row.LocalName != "c" || row.NamespaceURI != SheetNs)
                        continue;
                    var column = Column(row.GetAttribute("r")) ?? cells.Count;
                    if (column >= MaxColumns)
                        continue;
                    var type = row.GetAttribute("t");
                    var style = int.TryParse(row.GetAttribute("s"), out var st) ? st : 0;
                    var (value, inline) = CellContent(row);
                    while (cells.Count < column)
                        cells.Add("");
                    cells.Add(CellText(type, value, inline, style < formats.Count ? formats[style] : CellFormat.General, strings, date1904));
                }
                while (cells.Count > 0 && cells[^1].Length == 0)
                    cells.RemoveAt(cells.Count - 1);
                if (cells.Count > 0)
                {
                    lines.Add(string.Join(" | ", cells));
                    numbers.Add(rowNumber);
                }
            }
        }
        return (lines, numbers);
    }

    /// <summary>"C12" → 2 (Spalte C, ab 0).</summary>
    internal static int? Column(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return null;
        var column = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (c is >= 'A' and <= 'Z')
            {
                column = column * 26 + (c - 'A' + 1);
                letters++;
            }
            else
            {
                break;
            }
        }
        return letters == 0 ? null : column - 1;
    }

    /// <summary>Wert (&lt;v&gt;) und Text (&lt;is&gt;&lt;t&gt;) einer Zelle; der Leser steht danach am Ende der Zelle.</summary>
    private static (string? Value, string? Inline) CellContent(XmlReader row)
    {
        string? value = null;
        StringBuilder? inline = null;
        using var cell = row.ReadSubtree();
        var into = "";
        var phonetic = 0;
        while (cell.Read())
        {
            switch (cell.NodeType)
            {
                case XmlNodeType.Element when cell.LocalName == "rPh" && !cell.IsEmptyElement:
                    phonetic++;
                    break;
                case XmlNodeType.Element when !cell.IsEmptyElement:
                    into = cell.LocalName;
                    break;
                case XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace when phonetic == 0:
                    if (into == "v")
                        value += cell.Value;
                    else if (into == "t")
                        (inline ??= new StringBuilder()).Append(cell.Value);
                    break;
                case XmlNodeType.EndElement:
                    if (cell.LocalName == "rPh")
                        phonetic--;
                    into = "";
                    break;
            }
        }
        return (value, inline?.ToString());
    }

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    internal static string CellText(string? type, string? value, string? inline, CellFormat format, IReadOnlyList<string> strings, bool date1904)
    {
        switch (type)
        {
            case "s":
                return int.TryParse(value, out var index) && index >= 0 && index < strings.Count ? Clean(strings[index]) : "";
            case "inlineStr":
                return Clean(inline ?? "");
            case "str" or "e":
                return Clean(value ?? "");
            case "b":
                return value == "1" ? "WAHR" : "FALSCH";
            case "d":
                return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? FormatDate(d, d.TimeOfDay != TimeSpan.Zero) : value ?? "";
        }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return Clean(value ?? inline ?? "");
        switch (format)
        {
            case CellFormat.Date or CellFormat.DateTime when number is >= 0 and < 2958466:
                var date = DateTime.FromOADate(number + (date1904 ? 1462 : 0));
                return FormatDate(date, format == CellFormat.DateTime && date.TimeOfDay != TimeSpan.Zero);
            case CellFormat.Time when number is >= 0 and < 1:
                return DateTime.FromOADate(number).ToString("HH:mm", German);
            case CellFormat.Percent:
                return (number * 100).ToString("0.##", German) + " %";
            default:
                return Math.Abs(number) >= 1e15 ? number.ToString("G15", German) : number.ToString("0.##########", German);
        }
    }

    private static string FormatDate(DateTime date, bool withTime) => date.ToString(withTime ? "dd.MM.yyyy HH:mm" : "dd.MM.yyyy", German);

    private static string Clean(string text) => text.Replace('\n', ' ').Replace('\r', ' ').Replace('|', '/').Trim();

    internal enum CellFormat { General, Date, DateTime, Time, Percent }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var strings = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is not { } entry)
            return strings;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, Safe);
        var current = new StringBuilder();
        var inText = false;
        var phonetic = 0;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.LocalName == "rPh" && !reader.IsEmptyElement:
                    phonetic++;
                    break;
                case XmlNodeType.Element when reader.LocalName == "t":
                    inText = !reader.IsEmptyElement && phonetic == 0;
                    break;
                case XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace when inText:
                    current.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement when reader.LocalName == "t":
                    inText = false;
                    break;
                case XmlNodeType.EndElement when reader.LocalName == "rPh":
                    phonetic--;
                    break;
                case XmlNodeType.EndElement when reader.LocalName == "si":
                    strings.Add(current.ToString());
                    current.Clear();
                    break;
                case XmlNodeType.Element when reader.LocalName == "si" && reader.IsEmptyElement:
                    strings.Add("");
                    break;
            }
        }
        return strings;
    }

    /// <summary>Welche Zellformate Datum, Uhrzeit oder Prozent sind (styles.xml: cellXfs → numFmtId).</summary>
    private static List<CellFormat> CellFormats(ZipArchive zip)
    {
        var result = new List<CellFormat>();
        if (LoadXml(zip, "xl/styles.xml") is not { } styles)
            return result;
        XNamespace s = SheetNs;
        var custom = styles.Descendants(s + "numFmt")
            .Where(f => int.TryParse((string?)f.Attribute("numFmtId"), out _))
            .ToDictionary(f => int.Parse((string)f.Attribute("numFmtId")!), f => (string?)f.Attribute("formatCode") ?? "");
        foreach (var xf in styles.Descendants(s + "cellXfs").Elements(s + "xf"))
        {
            var id = int.TryParse((string?)xf.Attribute("numFmtId"), out var n) ? n : 0;
            result.Add(custom.TryGetValue(id, out var code) ? FormatOf(code) : BuiltIn(id));
        }
        return result;
    }

    private static CellFormat BuiltIn(int id) => id switch
    {
        9 or 10 => CellFormat.Percent,
        14 or 15 or 16 or 17 => CellFormat.Date,
        18 or 19 or 20 or 21 or 45 or 46 or 47 => CellFormat.Time,
        22 => CellFormat.DateTime,
        >= 27 and <= 36 or >= 50 and <= 58 => CellFormat.Date,
        _ => CellFormat.General,
    };

    /// <summary>Ein eigenes Zahlenformat wie "dd.mm.yyyy", "0.0%" oder "#,##0 €" deuten.</summary>
    internal static CellFormat FormatOf(string code)
    {
        // Text in Anführungszeichen, \x und [Farbe]/[$€-407] zählen nicht – [h], [mm] schon.
        var plain = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\\\.|\\[(?![hms]+\\])[^\\]]*\\]", "").ToLowerInvariant();
        var section = plain.Split(';')[0];
        if (section.Contains('%'))
            return CellFormat.Percent;
        var date = section.Contains('y') || section.Contains('d');
        var time = section.Contains('h') || section.Contains('s');
        if (date)
            return time ? CellFormat.DateTime : CellFormat.Date;
        if (time)
            return CellFormat.Time;
        // "mm" allein ist ein Monat (Datum), "m" neben h/s eine Minute – ohne y/d/h/s bleibt es ein Datum, wenn ein m da ist.
        return section.Contains('m') && !section.Contains('0') && !section.Contains('#') ? CellFormat.Date : CellFormat.General;
    }

    // ── PowerPoint ─────────────────────────────────────────────────────────────────────────────────

    private const string DrawingNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string PresentationNs = "http://schemas.openxmlformats.org/presentationml/2006/main";

    /// <summary>Jede Folie ein Stück: ihr Text Absatz für Absatz, dazu die Notizen.</summary>
    private static Document PowerPoint(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var presentation = LoadXml(zip, "ppt/presentation.xml") ?? throw new DocumentException($"{Path.GetFileName(path)} ist keine PowerPoint-Datei.");
        XNamespace p = PresentationNs, r = RelNs;
        var targets = Relationships(zip, "ppt/_rels/presentation.xml.rels", "ppt/");
        var parts = new List<DocumentPart>();
        foreach (var id in presentation.Descendants(p + "sldId").Select(e => (string?)e.Attribute(r + "id")))
        {
            ct.ThrowIfCancellationRequested();
            if (id is null || !targets.TryGetValue(id, out var target) || LoadXml(zip, target) is not { } slide)
                continue;
            var number = parts.Count + 1;
            var lines = SlideText(slide, notes: false);
            var folder = target[..(target.LastIndexOf('/') + 1)];
            var rels = Relationships(zip, folder + "_rels/" + target[(target.LastIndexOf('/') + 1)..] + ".rels", folder);
            var notesTarget = rels.Values.FirstOrDefault(t => t.Contains("notesSlide", StringComparison.OrdinalIgnoreCase));
            if (notesTarget is not null && LoadXml(zip, notesTarget) is { } notes && SlideText(notes, notes: true) is { Count: > 0 } said)
                lines.Add("Notizen: " + string.Join(" ", said));
            parts.Add(new DocumentPart(number, $"Folie {number}", lines));
        }
        if (parts.Count == 0)
            throw new DocumentException($"{Path.GetFileName(path)} enthält keine Folien.");
        return new Document("PowerPoint", "Folie", parts.Count, parts);
    }

    /// <summary>Die Absätze einer Folie (Tabellenzeilen als "Zelle | Zelle"); bei Notizen ohne Platzhalter für Folie und Nummer.</summary>
    private static List<string> SlideText(XDocument slide, bool notes)
    {
        XNamespace a = DrawingNs, p = PresentationNs;
        var lines = new List<string>();
        foreach (var shape in slide.Descendants().Where(e => e.Name == p + "sp" || e.Name == p + "graphicFrame"))
        {
            if (notes && shape.Descendants(p + "ph").Attributes("type").FirstOrDefault()?.Value is not ("body" or null))
                continue;
            foreach (var row in shape.Descendants(a + "tr"))
            {
                var cells = row.Elements(a + "tc").Select(tc => string.Join(" ", tc.Descendants(a + "p").Select(Paragraph)).Trim()).ToList();
                if (cells.Any(c => c.Length > 0))
                    lines.Add(string.Join(" | ", cells));
            }
            foreach (var paragraph in shape.Descendants(a + "p").Where(e => !e.Ancestors(a + "tbl").Any()))
            {
                var text = Paragraph(paragraph);
                if (text.Length > 0)
                    lines.Add(text);
            }
        }
        return lines;

        string Paragraph(XElement paragraph) =>
            string.Concat(paragraph.Descendants().Select(e => e.Name == a + "t" ? e.Value : e.Name == a + "br" ? "\n" : "")).Trim();
    }

    // ── Gemeinsam ──────────────────────────────────────────────────────────────────────────────────

    private static XDocument? LoadXml(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry)
            return null;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, Safe);
        return XDocument.Load(reader);
    }

    /// <summary>Beziehungen einer Datei im Zip: Id → Pfad im Zip ("rId2" → "ppt/slides/slide1.xml").</summary>
    private static Dictionary<string, string> Relationships(ZipArchive zip, string name, string folder)
    {
        var result = new Dictionary<string, string>();
        if (LoadXml(zip, name) is not { } rels)
            return result;
        XNamespace ns = PackageRelNs;
        foreach (var rel in rels.Descendants(ns + "Relationship"))
        {
            if ((string?)rel.Attribute("Id") is not { } id || (string?)rel.Attribute("Target") is not { } target || (string?)rel.Attribute("TargetMode") == "External")
                continue;
            result[id] = Normalize(target.StartsWith('/') ? target[1..] : folder + target);
        }
        return result;
    }

    /// <summary>"ppt/slides/../notesSlides/n1.xml" → "ppt/notesSlides/n1.xml".</summary>
    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else if (part is not ("." or ""))
            {
                parts.Add(part);
            }
        }
        return string.Join('/', parts);
    }
}
