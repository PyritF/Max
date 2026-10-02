using System.IO.Compression;
using Max.Llm;
using Max.Tools;
using Max.Tools.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Max.Tests;

/// <summary>Dokumente lesen: PDF, Word, Excel, PowerPoint, lange Textdateien – Anfang, Seiten, Zeilen, Suche.</summary>
public sealed class DocumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("max-dok-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Task<string> Read(string argument, Func<IVision?>? vision = null) =>
        new ReadFileTool(() => _dir, vision).RunAsync(argument, CancellationToken.None);

    // ── Hilfen: Dokumente bauen ──

    private string Pdf(string name, params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            for (var i = 0; i < lines.Length; i++)
                page.AddText(lines[i], 10, new PdfPoint(40, 800 - i * 14), font);
        }
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static string[] Filler(int page) =>
        Enumerable.Range(1, 30).Select(i => $"Seite {page} Absatz {i}: Allgemeine Bestimmungen und weitere Angaben zum Vertrag.").ToArray();

    private string Zip(string name, params (string Entry, string Xml)[] entries)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, xml) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(xml);
        }
        return path;
    }

    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    private string Docx(string name, string body) =>
        Zip(name, ("word/document.xml", $"<w:document {W}><w:body>{body}</w:body></w:document>"));

    private const string S = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private string Xlsx(string name, string[] shared, string styles, params (string Sheet, string Rows)[] sheets)
    {
        var entries = new List<(string, string)>
        {
            ("xl/workbook.xml", $"<workbook {S}><sheets>{string.Concat(sheets.Select((s, i) => $"<sheet name=\"{s.Sheet}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"))}</sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"x/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) + "</Relationships>"),
            ("xl/sharedStrings.xml", $"<sst {S}>{string.Concat(shared.Select(t => $"<si><t>{t}</t></si>"))}</sst>"),
            ("xl/styles.xml", $"<styleSheet {S}>{styles}</styleSheet>"),
        };
        entries.AddRange(sheets.Select((s, i) => ($"xl/worksheets/sheet{i + 1}.xml", $"<worksheet {S}><sheetData>{s.Rows}</sheetData></worksheet>")));
        return Zip(name, [.. entries]);
    }

    private const string P = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private static string Shape(string text, string? placeholder = null) =>
        $"<p:sp>{(placeholder is null ? "" : $"<p:nvSpPr><p:nvPr><p:ph type=\"{placeholder}\"/></p:nvPr></p:nvSpPr>")}<p:txBody>" +
        string.Concat(text.Split('\n').Select(line => $"<a:p><a:r><a:t>{line}</a:t></a:r></a:p>")) + "</p:txBody></p:sp>";

    // ── Word ──

    [Fact]
    public async Task Word_ParagraphsAndTables()
    {
        Docx("brief.docx", """
            <w:p><w:r><w:t>Sehr geehrte </w:t></w:r><w:r><w:t>Damen und Herren,</w:t></w:r></w:p>
            <w:p><w:r><w:t>die Rechnung liegt bei.</w:t></w:r></w:p>
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Posten</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Betrag</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:p><w:r><w:t>Miete</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>850 €</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            """);

        var result = await Read("brief.docx");

        Assert.Contains("Word-Dokument", result);
        Assert.Contains("Sehr geehrte Damen und Herren,\ndie Rechnung liegt bei.", result);
        Assert.Contains("Posten | Betrag\nMiete | 850 €", result);
    }

    [Fact]
    public async Task Word_BreaksAndTabsInsideARun_AreKept()
    {
        Docx("lauf.docx", "<w:p><w:r><w:t>Eins</w:t><w:br/><w:t>Zwei</w:t><w:tab/><w:t>Drei</w:t></w:r></w:p>");
        Assert.Contains("Eins\nZwei\tDrei", await Read("lauf.docx"));
    }

    [Fact]
    public async Task Word_PagesFromWordsOwnPageBreaks()
    {
        var pages = Enumerable.Range(1, 6).Select(p =>
            string.Concat(Enumerable.Range(1, 20).Select(i => $"<w:p><w:r><w:t>Seite {p}, Absatz {i}: etwas längerer Text, damit die Seiten nicht alle in ein Ergebnis passen.</w:t></w:r></w:p>")));
        Docx("lang.docx", string.Join("<w:p><w:r><w:lastRenderedPageBreak/><w:t>Weiter</w:t></w:r></w:p>", pages));

        var start = await Read("lang.docx");
        var page4 = await Read("lang.docx | Seite 4");

        Assert.Contains("Word-Dokument, 6 Seiten", start);
        Assert.Contains("--- Seite 1 ---", start);
        Assert.Contains("| Seite ", start);
        Assert.Contains("Gezeigt: Seite", page4);
        Assert.Contains("Seite 4, Absatz 1", page4);
        Assert.DoesNotContain("Seite 3, Absatz 1:", page4);
    }

    // ── PDF ──

    [Fact]
    public async Task Pdf_Short_ShowsEverything()
    {
        Pdf("konto.pdf", ["Kontostand 1234 Euro"], ["Zweite Seite"]);

        var result = await Read("konto.pdf");

        Assert.Contains("PDF, 2 Seiten:", result);
        Assert.Contains("--- Seite 1 ---\nKontostand 1234 Euro", result);
        Assert.Contains("--- Seite 2 ---\nZweite Seite", result);
    }

    [Fact]
    public async Task Pdf_Long_ShowsTheBeginning_AndHowToGoOn()
    {
        Pdf("vertrag.pdf", [.. Enumerable.Range(1, 40).Select(Filler)]);

        var result = await Read("vertrag.pdf");

        Assert.Contains("PDF, 40 Seiten", result);
        Assert.Contains("Gezeigt: Seiten 1–", result);
        Assert.Matches(@"`datei: vertrag\.pdf \| Seite \d+`", result);
        Assert.Contains("`datei: vertrag.pdf | Suchbegriff`", result);
        Assert.True(result.Length < ToolBox.MaxResultChars);
    }

    [Fact]
    public async Task Pdf_Search_FindsThePage_FarBehind()
    {
        var pages = Enumerable.Range(1, 40).Select(Filler).ToArray();
        // Die Standardschrift der Test-PDF kennt keine Umlaute – "Kuendigung" findet die Suche trotzdem.
        pages[26] = [.. pages[26], "Paragraph 12 Kuendigung: Die Kuendigungsfrist betraegt drei Monate zum Quartalsende."];
        Pdf("vertrag.pdf", pages);

        var result = await Read("vertrag.pdf | Wie lange ist die Kündigungsfrist?");

        Assert.Contains("Stellen zu „Wie lange ist die Kündigungsfrist?“", result);
        Assert.Contains("--- Seite 27 ---", result);
        Assert.Contains("drei Monate zum Quartalsende", result);
        Assert.Contains("--- Anfang (Seite 1) ---", result);
        Assert.Contains("`datei: vertrag.pdf | Seite 27`", result);
    }

    [Fact]
    public async Task Pdf_PageSelection_AndLimits()
    {
        Pdf("vertrag.pdf", [.. Enumerable.Range(1, 40).Select(Filler)]);

        var page = await Read("vertrag.pdf | Seite 30");
        var range = await Read("vertrag.pdf | Seiten 38-40");
        var end = await Read("vertrag.pdf | Ende");

        Assert.Contains("--- Seite 30 ---", page);
        Assert.DoesNotContain("--- Seite 29 ---", page);
        Assert.Contains("Gezeigt: Seiten 38–39", range);            // drei passen nicht ganz hinein
        Assert.Contains("`datei: vertrag.pdf | Seite 40`", range);
        Assert.Contains("--- Seite 40 ---", end);
        Assert.Contains("Es gibt nur 40 Seiten", await Read("vertrag.pdf | Seite 99"));
        Assert.Contains("keine Zeilennummern", await Read("vertrag.pdf | Zeile 10"));
    }

    [Fact]
    public async Task Pdf_NothingFound_SaysSo_AndShowsTheBeginning()
    {
        Pdf("vertrag.pdf", [.. Enumerable.Range(1, 40).Select(Filler)]);

        var result = await Read("vertrag.pdf | Haustierhaltung");

        Assert.Contains("Zu „Haustierhaltung“ steht darin nichts – hier der Anfang.", result);
        Assert.Contains("--- Seite 1 ---", result);
    }

    [Fact]
    public async Task ScannedPdf_IsReadByTheVision_OnlyWhenShown()
    {
        var builder = new PdfDocumentBuilder();
        var jpeg = Convert.FromBase64String(TinyJpeg);
        builder.AddPage(PageSize.A4).AddJpeg(jpeg, new PdfRectangle(0, 0, 595, 842));
        builder.AddPage(PageSize.A4).AddPng(Convert.FromBase64String(TinyPng), new PdfRectangle(0, 0, 595, 842));
        File.WriteAllBytes(Path.Combine(_dir, "scan.pdf"), builder.Build());
        var vision = new ReadingVision();

        var notYet = await Read("scan.pdf", () => null);              // Bild-Zusatz kommt noch
        var result = await Read("scan.pdf", () => vision);
        var blind = await Read("scan.pdf");

        Assert.Contains("(eingescannt, vom Bild abgelesen)\nRechnung Nr. 4711", result);
        Assert.Equal([".jpg", ".png"], vision.Seen.Select(Path.GetExtension));
        Assert.Contains(PdfScan.Question, vision.Questions[0]);
        Assert.Contains("Bildverständnis ist noch nicht auf diesem Rechner", blind);
        Assert.Contains("Bildverständnis ist noch nicht auf diesem Rechner", notYet);
        Assert.DoesNotContain("lässt sich nicht lesen", notYet);
    }

    [Fact]
    public void ScannedPdf_JpegBehindAnotherFilter_IsUnpacked()
    {
        // Selbsttest 50: Die Rechnung (wie von reportlab) hat [/ASCII85Decode /DCTDecode] – das JPEG kam nie beim Bild-Zusatz an.
        var image = PdfScan.PageImage(AudioTests.RepoFile("tests/rechnung-scan.pdf"), 1);

        Assert.NotNull(image);
        Assert.Equal(".jpg", image.Value.Extension);
        Assert.Equal([0xFF, 0xD8], image.Value.Bytes[..2]);
        Assert.True(image.Value.Bytes.Length > 50_000);
    }

    private sealed class ReadingVision : IVision
    {
        public List<string> Seen { get; } = [];
        public List<string> Questions { get; } = [];

        public Task<string> LookAsync(string imagePath, string question, CancellationToken ct, int maxTokens = VisionEngine.MaxAnswerTokens)
        {
            Assert.True(File.Exists(imagePath));
            Seen.Add(imagePath);
            Questions.Add(question);
            return Task.FromResult("Rechnung Nr. 4711\nBetrag: 99 Euro");
        }
    }

    // ── Textdateien ──

    [Fact]
    public async Task LongTextFile_ByLines()
    {
        var lines = Enumerable.Range(1, 1000).Select(i => i == 777 ? "    var geheimeZutat = \"Zimt\";" : $"    // Zeile {i}: gewöhnlicher Code").ToArray();
        File.WriteAllLines(Path.Combine(_dir, "Program.cs"), lines);

        var start = await Read("Program.cs");
        var middle = await Read("Program.cs | Zeile 500");
        var found = await Read("Program.cs | geheimeZutat");

        Assert.Contains("Textdatei, 1000 Zeilen", start);
        Assert.Contains("Gezeigt: Zeile 1–", start);
        Assert.Matches(@"`datei: Program\.cs \| Zeile \d+`", start);
        Assert.StartsWith("    // Zeile 500:", middle[(middle.IndexOf('\n') + 1)..]);
        Assert.Contains("--- Zeile 7", found);
        Assert.Contains("\"Zimt\"", found);
        Assert.Contains("Seiten", await Read("Program.cs | Seite 2"));
    }

    [Fact]
    public async Task OldWindowsText_IsDecoded()
    {
        File.WriteAllBytes(Path.Combine(_dir, "alt.txt"), [0x47, 0x72, 0xFC, 0xDF, 0x65, 0x20, 0x80]);      // "Grüße €" in Windows-1252
        Assert.Contains("Grüße €", await Read("alt.txt"));
    }

    // ── Excel ──

    [Fact]
    public async Task Excel_SheetsWithDatesAndPercent()
    {
        Xlsx("umsatz.xlsx", ["Monat", "Umsatz", "Anteil", "Januar"],
            """<numFmts count="1"><numFmt numFmtId="164" formatCode="dd/mm/yyyy"/></numFmts><cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="164"/><xf numFmtId="9"/></cellXfs>""",
            ("Umsatz", """
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c><c r="D1" t="inlineStr"><is><t>Stichtag</t></is></c></row>
                <row r="2"><c r="A2" t="s"><v>3</v></c><c r="B2"><v>1200.5</v></c><c r="C2" s="2"><v>0.125</v></c><c r="D2" s="1"><v>46037</v></c></row>
                <row r="4"><c r="B4"><f>SUM(B2:B3)</f><v>1200.5</v></c></row>
                """),
            ("Leer", ""));

        var result = await Read("umsatz.xlsx");

        Assert.Contains("Excel-Tabelle, 2 Blätter", result);
        Assert.Contains("--- Blatt „Umsatz“ ---\nMonat | Umsatz | Anteil | Stichtag\nJanuar | 1200,5 | 12,5 % | 15.01.2026\n | 1200,5", result);
    }

    [Fact]
    public async Task Excel_BigSheet_SearchShowsRowsAndHeader()
    {
        var rows = string.Concat(Enumerable.Range(1, 3000).Select(i => i == 1
            ? """<row r="1"><c r="A1" t="inlineStr"><is><t>Artikel</t></is></c><c r="B1" t="inlineStr"><is><t>Preis</t></is></c></row>"""
            : $"""<row r="{i}"><c r="A{i}" t="inlineStr"><is><t>{(i == 2345 ? "Kiwi" : $"Apfel {i}")}</t></is></c><c r="B{i}"><v>{i}</v></c></row>"""));
        Xlsx("obst.xlsx", [], "", ("Obst", rows));

        var found = await Read("obst.xlsx | Kiwi");
        var rowsFrom = await Read("obst.xlsx | Blatt Obst, Zeile 2000");

        Assert.Contains("--- Blatt „Obst“, Zeile 23", found);
        Assert.Contains("(Kopfzeile: Artikel | Preis)", found);
        Assert.Contains("Kiwi | 2345", found);
        Assert.Contains("Gezeigt: Blatt „Obst“, Zeile 2000–", rowsFrom);
        Assert.Contains("\nApfel 2000 | 2000\n", rowsFrom);
        Assert.Contains("Ein Blatt „Gemüse“ gibt es nicht – nur „Obst“.", await Read("obst.xlsx | Blatt Gemüse"));
    }

    // ── PowerPoint ──

    [Fact]
    public async Task PowerPoint_SlidesWithNotes()
    {
        Zip("vortrag.pptx",
            ("ppt/presentation.xml", $"<p:presentation {P}><p:sldIdLst><p:sldId id=\"257\" r:id=\"rId3\"/><p:sldId id=\"256\" r:id=\"rId2\"/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId2" Type="x/slide" Target="slides/slide2.xml"/><Relationship Id="rId3" Type="x/slide" Target="slides/slide1.xml"/></Relationships>"""),
            ("ppt/slides/slide1.xml", $"<p:sld {P}><p:cSld><p:spTree>{Shape("Quartalszahlen\nUmsatz +12 %")}</p:spTree></p:cSld></p:sld>"),
            ("ppt/slides/slide2.xml", $"<p:sld {P}><p:cSld><p:spTree>{Shape("Ausblick 2027")}</p:spTree></p:cSld></p:sld>"),
            ("ppt/slides/_rels/slide1.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="x/notesSlide" Target="../notesSlides/notesSlide1.xml"/></Relationships>"""),
            ("ppt/notesSlides/notesSlide1.xml", $"<p:notes {P}><p:cSld><p:spTree>{Shape("1", "sldNum")}{Shape("Hier kurz innehalten.", "body")}</p:spTree></p:cSld></p:notes>"));

        var result = await Read("vortrag.pptx");

        Assert.Contains("PowerPoint, 2 Folien", result);
        Assert.Contains("--- Folie 1 ---\nQuartalszahlen\nUmsatz +12 %\nNotizen: Hier kurz innehalten.\n--- Folie 2 ---\nAusblick 2027", result);
    }

    // ── Auswahl und Suche ──

    [Theory]
    [InlineData("Seite 7", "Parts(7,)")]
    [InlineData("Seiten 3-5", "Parts(3,5)")]
    [InlineData("S. 4", "Parts(4,)")]
    [InlineData("folie 2", "Parts(2,)")]
    [InlineData("Teil 3", "Parts(3,)")]
    [InlineData("Zeile 120", "Lines(120,,)")]
    [InlineData("Zeilen 10 bis 20", "Lines(10,20,)")]
    [InlineData("Blatt Umsatz, Zeile 15", "Lines(15,,Umsatz)")]
    [InlineData("Blatt „Umsatz 2026“", "Lines(0,,Umsatz 2026)")]
    [InlineData("Ende", "End")]
    [InlineData("", "Start")]
    [InlineData("Fasse das bitte zusammen", "Start")]
    [InlineData("Kündigungsfrist", "Search(kuendigungsfrist)")]
    [InlineData("Wie hoch sind die Nebenkosten?", "Search(hoch,nebenkost)")]
    public void Selector_IsUnderstood(string selector, string expected)
    {
        var text = DocumentView.Parse(selector) switch
        {
            DocumentView.Selection.Parts p => $"Parts({p.From},{p.To})",
            DocumentView.Selection.Lines l => $"Lines({l.From},{l.To},{l.Sheet})",
            DocumentView.Selection.Search s => $"Search({string.Join(",", s.Terms)})",
            var other => other.GetType().Name,
        };
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Search_German_Friendly()
    {
        Assert.Equal(1, DocumentSearch.Count(DocumentSearch.Fold("Die Kündigungsfrist"), DocumentSearch.Stem(DocumentSearch.Fold("Fristen"))));
        Assert.Equal(1, DocumentSearch.Count(DocumentSearch.Fold("Kündigung"), "kuendigung"));
        Assert.Equal(0, DocumentSearch.Count("120250 euro", "2025"));
        Assert.Equal(1, DocumentSearch.Count("am 1.1.2025 faellig", "2025"));
        Assert.Equal("cafe", DocumentSearch.Fold("Café"));
    }

    // ── Hineingezogen ──

    [Fact]
    public async Task DraggedDocument_WithAQuestion_SearchesIt()
    {
        var pages = Enumerable.Range(1, 40).Select(Filler).ToArray();
        pages[33] = [.. pages[33], "Die Nebenkosten betragen monatlich 180 Euro."];
        var path = Pdf("miete.pdf", pages);

        var call = Assert.Single(Attachments.Calls($"\"{path}\" Wie hoch sind die Nebenkosten?", _dir));
        var result = await new ReadFileTool(() => _dir).RunAsync(call.Argument, CancellationToken.None);

        Assert.Equal(new ToolCall("datei", $"{path} | Wie hoch sind die Nebenkosten?"), call);
        Assert.Contains("180 Euro", result);
    }

    [Fact]
    public async Task Images_AndBrokenOrOldFormats_GiveASentence()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "kaputt.pdf"), "kein pdf");
        await File.WriteAllTextAsync(Path.Combine(_dir, "alt.doc"), "x");
        await File.WriteAllBytesAsync(Path.Combine(_dir, "foto.png"), [0x89, 0x50, 0x4E, 0x47]);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "programm.exe"), [0x4D, 0x5A, 0, 0]);
        Assert.Contains("ließ sich nicht lesen", await Read("kaputt.pdf"));
        Assert.Contains("alten Office-Format", await Read("alt.doc"));
        Assert.Contains("dafür gibt es \"bild\"", await Read("foto.png"));
        Assert.Contains("keine Textdatei", await Read("programm.exe"));
    }

    internal const string TinyJpeg =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/" +
        "2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCAAQABADASIAAhEBAxEB/8QA" +
        "HwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkK" +
        "FhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXG" +
        "x8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC" +
        "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOE" +
        "hYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDHooorhPqD/9k=";

    internal const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAGUlEQVR4nGM8ISfHQApgIkn1qIZRDUNKAwBPHwEkcYw1mAAAAABJRU5ErkJggg==";
}
