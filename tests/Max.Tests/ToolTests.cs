using Max.Chat;
using Max.Llm;
using Max.Persona;
using Max.Setup;
using Max.Tools;
using Max.Ui;
using Spectre.Console.Testing;

namespace Max.Tests;

public class CalculatorTests
{
    [Theory]
    [InlineData("123456789 * 987654321", "121932631112635269")]
    [InlineData("121.932.631.112.635.269 / 987654321", "123456789")]
    [InlineData("1.234,5 * 2", "2469")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("2^10", "1024")]
    [InlineData("2^3^2", "512")]
    [InlineData("-3 + 5", "2")]
    [InlineData("1/3", "0.333333333333")]
    [InlineData("17,5 × 2", "35")]
    [InlineData("1.000.000 / 4", "250000")]
    [InlineData("200 * 15%", "30")]
    [InlineData("sqrt(16) + abs(-2)", "6")]
    [InlineData("10 : 4", "2.5")]
    public void Evaluate(string expression, string expected) => Assert.Equal(expected, CalculatorTool.Evaluate(expression));

    [Theory]
    [InlineData("121932631112635269", "121.932.631.112.635.269")]
    [InlineData("-1234567", "-1.234.567")]
    [InlineData("123456", "123456")]
    [InlineData("0.333333333333", "0.333333333333")]
    public void LongResults_AreGroupedInThrees(string result, string expected) => Assert.Equal(expected, CalculatorTool.Grouped(result));

    [Fact]
    public void TooBig_ForExact_IsApproximate() => Assert.StartsWith("ungefähr 1E+40", CalculatorTool.Evaluate("10^40"));

    [Theory]
    [InlineData("2 +", "hört mittendrin auf")]
    [InlineData("(1 + 2", "Klammer")]
    [InlineData("foo(2)", "kenne ich nicht")]
    [InlineData("1/0", "Division durch null")]
    public async Task Errors_AreSentences(string expression, string expected) =>
        Assert.Contains(expected, await new CalculatorTool().RunAsync(expression, CancellationToken.None));
}

public sealed class FileToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "max-tools-" + Guid.NewGuid().ToString("N"));

    public FileToolTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, ".ssh"));
        File.WriteAllText(Path.Combine(_dir, "README.md"), "# Projekt\nEin Test.");
        File.WriteAllText(Path.Combine(_dir, ".env"), "PASSWORT=geheim");
        File.WriteAllText(Path.Combine(_dir, ".ssh", "config"), "Host x");
        File.WriteAllBytes(Path.Combine(_dir, "bild.png"), [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 1]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ListFolder_FoldersFirst_WithSizes()
    {
        var text = await new ListFolderTool(() => _dir).RunAsync(".", CancellationToken.None);
        Assert.True(text.IndexOf("- src/", StringComparison.Ordinal) < text.IndexOf("- README.md", StringComparison.Ordinal));
        Assert.Contains("README.md (19 B)", text);
    }

    [Fact]
    public async Task ReadFile_RelativeToTheWorkingDirectory()
    {
        var text = await new ReadFileTool(() => _dir).RunAsync("README.md", CancellationToken.None);
        Assert.Contains("# Projekt\nEin Test.", text);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".ssh/config")]
    [InlineData("schluessel.pem")]
    public async Task Secrets_AreNeverRead(string path)
    {
        var text = await new ReadFileTool(() => _dir).RunAsync(path, CancellationToken.None);
        Assert.DoesNotContain("geheim", text);
        Assert.Contains("Zugangsdaten", text);
    }

    [Fact]
    public async Task BinaryAndMissing_GiveAHint()
    {
        Assert.Contains("keine Textdatei", await new ReadFileTool(() => _dir).RunAsync("bild.png", CancellationToken.None));
        Assert.Contains("gibt es nicht", await new ReadFileTool(() => _dir).RunAsync("fehlt.txt", CancellationToken.None));
        Assert.Contains("ist ein Ordner", await new ReadFileTool(() => _dir).RunAsync("src", CancellationToken.None));
    }
}

public class WebToolTests
{
    private const string SearchPage = """
        <div class="result results_links results_links_deep web-result ">
          <h2 class="result__title">
            <a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fde.wikipedia.org%2Fwiki%2FFu%C3%9Fball%2DWeltmeisterschaft_2022&amp;rut=abc">Fußball-Weltmeisterschaft 2022 – <b>Wikipedia</b></a>
          </h2>
          <a class="result__snippet" href="//duckduckgo.com/l/?uddg=x">Weltmeister wurde <b>Argentinien</b> nach einem Sieg im Elfmeterschießen gegen Frankreich.</a>
        </div>
        <div class="result">
          <a rel="nofollow" class="result__a" href="https://duckduckgo.com/y.js?ad_provider=x">Werbung</a>
          <a class="result__snippet">Kaufen!</a>
        </div>
        """;

    [Fact]
    public void Search_ReadsTitleUrlAndSnippet_WithoutAds()
    {
        var result = Assert.Single(WebSearchTool.Parse(SearchPage));
        Assert.Equal("Fußball-Weltmeisterschaft 2022 – Wikipedia", result.Title);
        Assert.Equal("https://de.wikipedia.org/wiki/Fußball-Weltmeisterschaft_2022", result.Url);
        Assert.StartsWith("Weltmeister wurde Argentinien", result.Snippet);
    }

    [Fact]
    public void Page_ToText_WithoutScriptsAndMenus()
    {
        var (title, text) = WebPage.ToText("""
            <html><head><title>Test &amp; mehr</title><style>p{}</style></head>
            <body><nav>Menü</nav><h1>Überschrift</h1><p>Erster&nbsp;Absatz.</p><script>alert(1)</script><p>Zweiter <b>Absatz</b>.</p></body></html>
            """);
        Assert.Equal("Test & mehr", title);
        Assert.Equal("Überschrift\nErster Absatz.\nZweiter Absatz .", text);
    }
}

public class ToolBoxTests
{
    private sealed class SlowTool : ITool
    {
        public string Name => "langsam";
        public string? Argument => null;
        public string Description => "";
        public string Describe(string argument) => "";

        public async Task<string> RunAsync(string argument, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        }
    }

    [Fact]
    public void ToolCall_Parse()
    {
        Assert.Equal(new ToolCall("websuche", "Einwohner Graz"), ToolCall.Parse("Websuche: Einwohner Graz\n"));
        Assert.Equal(new ToolCall("uhrzeit", ""), ToolCall.Parse("uhrzeit\n"));
        Assert.Equal("```werkzeug\nrechnen: 1+1\n```", new ToolCall("rechnen", "1+1").Text);
    }

    [Fact]
    public async Task UnknownTool_MissingArgument_AndLongResults()
    {
        var box = new ToolBox([new CalculatorTool()]);
        Assert.Contains("gibt es nicht", await box.RunAsync(new ToolCall("zaubern", ""), CancellationToken.None));
        Assert.Contains("braucht eine Angabe", await box.RunAsync(new ToolCall("rechnen", ""), CancellationToken.None));
        Assert.EndsWith("(gekürzt, 2 Zeichen mehr)", ToolBox.Shorten(new string('x', ToolBox.MaxResultChars + 2), ToolBox.MaxResultChars));
    }

    [Fact]
    public async Task Cancelling_TheAnswer_CancelsTheTool()
    {
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ToolBox([new SlowTool()]).RunAsync(new ToolCall("langsam", ""), cts.Token));
    }

    [Fact]
    public void Grammar_KnowsEveryTool()
    {
        using var http = new HttpClient();
        var box = ToolBox.CreateDefault(http, () => DateTime.Now, () => ".");
        var grammar = AnswerGrammar.Build(box.GrammarRule());
        Assert.Contains("root ::= \"```\" w-werkzeug | answer", grammar);
        foreach (var tool in box.All)
            Assert.Contains($"\"{tool.Name}", grammar);
        Assert.DoesNotContain("werkzeug", AnswerGrammar.Gbnf);
    }

    [Fact]
    public void ToolOnly_Grammar_AllowsNothingButACall()
    {
        using var http = new HttpClient();
        var box = ToolBox.CreateDefault(http, () => DateTime.Now, () => ".");
        Assert.Contains("root ::= \"```\" w-werkzeug\n", AnswerGrammar.Build(box.GrammarRule(), toolOnly: true));
    }

    [Theory]
    // Aus dem Selbsttest: angekündigt, aber im Kopf gerechnet.
    [InlineData("Der Nutzer möchte eine Rechnung. Ich sollte das Werkzeug `rechnen` verwenden, um das Ergebnis zu berechnen.", "rechnen")]
    [InlineData("Die drei größten Städte sind Wien, Graz und Linz. Ich rufe `websuche` auf, um die aktuellen Einwohnerzahlen zu bekommen.", "websuche")]
    [InlineData("Ich nutze `datei: README.md` und fasse zusammen.", "datei")]
    [InlineData("Dafür verwende ich das Werkzeug „ordner“.", "ordner")]
    [InlineData("Ich habe das Werkzeug `rechnen` für größere Berechnungen. Für einfache Rechnungen im Kopf kann ich das auch.", null)]
    [InlineData("Ich sollte keine Werkzeuge verwenden, da es um allgemeines Wissen geht.", null)]
    [InlineData("Ich brauche `websuche` hier nicht, das weiß ich.", null)]
    [InlineData("Ich sollte die Datei lesen und das System verwenden.", null)]
    public void IntendedTool_OnlyAnnouncedUse(string thought, string? expected)
    {
        using var http = new HttpClient();
        Assert.Equal(expected, ToolBox.CreateDefault(http, () => DateTime.Now, () => ".").IntendedTool(thought));
    }

    [Fact]
    public void SystemPrompt_ListsTheTools_InTheStablePart()
    {
        using var http = new HttpClient();
        var box = ToolBox.CreateDefault(http, () => DateTime.Now, () => ".");
        var system = new SystemSnapshot(new DateTime(2026, 9, 28, 9, 0, 0), UserIdentity.Create("alex", "Alex Beispiel"), "Windows 11", 16,
            new HardwareInfo(32L << 30, null), @"C:\Users\alex");
        var prompt = SystemPrompt.BuildParts(system, tools: box);

        var section = prompt.Text.IndexOf("## Werkzeuge", StringComparison.Ordinal);
        Assert.True(section > 0 && section < prompt.StableLength);
        Assert.Contains("- `websuche: <Suchbegriffe>`", prompt.Text);
        Assert.DoesNotContain("{{", prompt.Text);
        Assert.DoesNotContain("Werkzeuge", SystemPrompt.Build(system));
    }
}

public class ToolRoundTests
{
    [Fact]
    public async Task ToolCall_IsRunHidden_ThenTheAnswerUsesTheResult()
    {
        var model = new FakeModel("```werkzeug\n", "rechnen: 2+3\n", "```\n", "Das sind ", "5.", null);
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => false, Tools: new ToolBox([new CalculatorTool()])));
        var conversation = new Conversation();
        conversation.AddUser("Was ist 2+3?");

        var chunks = new List<ReplyChunk>();
        await foreach (var chunk in backend.StreamReplyAsync(conversation, CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal("Rechne: 2+3", Assert.Single(chunks, c => c.IsTool).Text);
        Assert.Equal("Das sind 5.", string.Concat(chunks.Where(c => !c.IsTool && !c.IsThinking).Select(c => c.Text)));
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], conversation.Messages.Select(m => m.Role));
        Assert.Equal("```werkzeug\nrechnen: 2+3\n```", conversation.Messages[1].Content);
        Assert.Contains("<tool_response>\n2+3 = 5\n</tool_response>", model.Decode(model.LastPromptBeforeSampler));
    }

    private static async Task<(List<ReplyChunk> Chunks, Conversation Conversation)> Run(params string?[] script)
    {
        var model = new FakeModel(script);
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => false, Tools: new ToolBox([new CalculatorTool()])));
        var conversation = new Conversation();
        conversation.AddUser("?");
        var chunks = new List<ReplyChunk>();
        await foreach (var chunk in backend.StreamReplyAsync(conversation, CancellationToken.None))
            chunks.Add(chunk);
        await backend.CompleteAsync();
        return (chunks, conversation);
    }

    private static string Text(List<ReplyChunk> chunks) => string.Concat(chunks.Where(c => !c.IsTool && !c.IsThinking).Select(c => c.Text));

    [Theory]
    [InlineData("```python\n", "rechnen: 2+3\n")]
    [InlineData("```bash\n", "werkzeug\nrechnen: \"2+3\"\n")]
    [InlineData("\n\n```\n", "rechnen: 2+3\n")]
    [InlineData("```werkzeug\n", "rechnen: 2+3\n")]
    public async Task ToolCall_AsCodeBlock_IsRunToo(string header, string body)
    {
        var (chunks, conversation) = await Run(header, body, "```\n", "Fünf.", null);

        Assert.Single(chunks, c => c.IsTool);
        Assert.Equal("Fünf.", Text(chunks).Trim());
        Assert.Equal("```werkzeug\nrechnen: 2+3\n```", conversation.Messages[1].Content);   // im Verlauf immer die richtige Form
    }

    [Fact]
    public async Task ToolCall_EndingWithoutNewline_IsRunToo()
    {
        // So endet das echte Modell: "```" und dann sofort Schluss.
        var (chunks, conversation) = await Run("```python\n", "rechnen: 2+3\n", "```", null, "Fünf.", null);

        Assert.Single(chunks, c => c.IsTool);
        Assert.Equal("Fünf.", Text(chunks).Trim());
        Assert.Equal(ChatRole.Tool, conversation.Messages[2].Role);
    }

    [Fact]
    public async Task RealCode_AtTheStart_IsShownAsCode()
    {
        var (chunks, conversation) = await Run("```python\n", "print(1)\n", "```\n", "Fertig.", null);

        Assert.DoesNotContain(chunks, c => c.IsTool);
        Assert.Equal("```python\nprint(1)\n```\nFertig.", Text(chunks));
        Assert.Single(conversation.Messages);
    }

    [Fact]
    public async Task LongCode_AtTheStart_FlowsWithoutWaitingForTheEnd()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"print({i})  # Zeile {i}\n").ToArray();
        var (chunks, _) = await Run(["```python\n", .. lines, "```\n", "Fertig.", null]);

        Assert.DoesNotContain(chunks, c => c.IsTool);
        Assert.Equal("```python\n" + string.Concat(lines) + "```\nFertig.", Text(chunks));
        Assert.True(chunks.Count(c => !c.IsTool) > 3);      // nicht erst am Ende in einem Stück
    }

    [Fact]
    public async Task WithoutTools_TheBlockIsNeverRun()
    {
        var model = new FakeModel("```werkzeug\n", "rechnen: 2+3\n", "```\n", "Fertig.", null);
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => false));
        var conversation = new Conversation();
        conversation.AddUser("Was ist 2+3?");

        var chunks = new List<ReplyChunk>();
        await foreach (var chunk in backend.StreamReplyAsync(conversation, CancellationToken.None))
            chunks.Add(chunk);

        Assert.DoesNotContain(chunks, c => c.IsTool);
        Assert.Equal("Fertig.", string.Concat(chunks.Select(c => c.Text)).Trim());
        Assert.Single(conversation.Messages);
    }

    [Fact]
    public async Task ChatView_ShowsWhatMaxIsDoing()
    {
        var console = new TestConsole();
        var view = new ChatView(console, animate: false);
        static async IAsyncEnumerable<ReplyChunk> Chunks()
        {
            yield return new ReplyChunk("Suche im Web: Wetter Graz", IsTool: true);
            await Task.Yield();
            yield return new ReplyChunk("Sonnig.");
        }

        var text = await view.StreamReplyAsync(Chunks(), CancellationToken.None);

        Assert.Equal("Sonnig.", text);
        Assert.Contains("⌕ Suche im Web: Wetter Graz", console.Output);
    }
}

public class ImageAndAttachmentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("max-bild-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name, string content = "x")
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private sealed class FakeVision : IVision
    {
        public List<(string Path, string Question)> Seen { get; } = [];

        public Task<string> LookAsync(string imagePath, string question, CancellationToken ct)
        {
            Seen.Add((imagePath, question));
            return Task.FromResult("Ein roter Kreis und der Text MAX 42.");
        }
    }

    [Fact]
    public void DraggedPaths_AreFound_InEveryTerminalStyle()
    {
        var spaced = File("mein bild.png");
        var plain = File("notiz.txt");
        Assert.Equal([spaced], Attachments.Find($"\"{spaced}\" was ist das?", _dir, out var rest));
        Assert.Equal("was ist das?", rest);
        Assert.Equal([spaced], Attachments.Find($"'{spaced}'", _dir, out _));
        Assert.Equal([spaced], Attachments.Find(spaced.Replace(" ", "\\ ") + " bitte", _dir, out rest));
        Assert.Equal("bitte", rest);
        Assert.Equal([plain], Attachments.Find($"Lies {plain}", _dir, out _));
        Assert.Equal([plain], Attachments.Find(new Uri(plain).AbsoluteUri, _dir, out _));
    }

    [Fact]
    public void RelativePaths_CountWhenTheFileExists()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bilder"));
        var image = File(Path.Combine("bilder", "katze.png"));
        var readme = File("README.md");
        Assert.Equal([image], Attachments.Find("Schau dir bilder/katze.png an: Welche Farbe hat die Katze?", _dir, out var rest));
        Assert.Equal("Schau dir an: Welche Farbe hat die Katze?", rest);
        Assert.Equal([readme], Attachments.Find("Lies README.md.", _dir, out _));
        Assert.Empty(Attachments.Find("Das steht z.B. in foto.png oder auf example.com.", _dir, out _));
    }

    [Theory]
    [InlineData("Wie geht's? 1/2 und/oder /debug")]
    [InlineData("\"/gibt/es/nicht.png\" schau mal")]
    public void NoFiles_NoAttachments(string message) => Assert.Empty(Attachments.Find(message, _dir, out _));

    [Fact]
    public void SecretFiles_AreNeverAttached()
    {
        var secret = File(".env", "PASSWORT=1");
        Assert.Empty(Attachments.Find($"\"{secret}\"", _dir, out _));
    }

    [Fact]
    public void Calls_ImageGetsTheQuestion_TextFileIsRead()
    {
        var image = File("a.png");
        var text = File("b.md");
        var calls = Attachments.Calls($"\"{image}\" \"{text}\" Was steht da?", _dir);
        Assert.Equal(new ToolCall("bild", $"{image} | Was steht da?"), calls[0]);
        Assert.Equal(new ToolCall("datei", text), calls[1]);
    }

    [Fact]
    public async Task ImageTool_AsksTheVision_WithTheQuestion()
    {
        var image = File("a.png");
        var vision = new FakeVision();
        var result = await new ImageTool(() => vision, () => _dir).RunAsync("a.png | Welche Farbe?", CancellationToken.None);
        Assert.Contains("MAX 42", result);
        Assert.Equal((image, "Welche Farbe? Antworte auf Deutsch."), Assert.Single(vision.Seen));
    }

    [Fact]
    public async Task ImageTool_ExplainsWhatIsWrong()
    {
        File("a.txt");
        File("a.png");
        var tool = new ImageTool(() => null, () => _dir);
        Assert.Contains("gibt es nicht", await tool.RunAsync("fehlt.png", CancellationToken.None));
        Assert.Contains("kein Bild", await tool.RunAsync("a.txt", CancellationToken.None));
        Assert.Contains("noch nicht auf diesem Rechner", await tool.RunAsync("a.png", CancellationToken.None));
        Assert.Equal(("x.png", ""), ImageTool.Split("x.png"));
    }

    [Fact]
    public async Task DraggedImage_IsLookedAt_BeforeTheAnswer()
    {
        var image = File("foto.png");
        var vision = new FakeVision();
        var model = new FakeModel("Da steht MAX 42.", null);
        var tools = new ToolBox([new ImageTool(() => vision, () => _dir)], () => _dir);
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => false, Tools: tools));
        var conversation = new Conversation();
        conversation.AddUser($"\"{image}\" Was steht da drauf?");

        var chunks = new List<ReplyChunk>();
        await foreach (var chunk in backend.StreamReplyAsync(conversation, CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal("Sehe mir foto.png an", Assert.Single(chunks, c => c.IsTool).Text);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool], conversation.Messages.Select(m => m.Role));
        Assert.Equal("Da steht MAX 42.", string.Concat(chunks.Where(c => !c.IsTool).Select(c => c.Text)));
        Assert.Contains("MAX 42", conversation.Messages[2].Content);
        Assert.Contains("Was steht da drauf?", Assert.Single(vision.Seen).Question);
        Assert.Contains("<tool_response>", model.Decode(model.LastPromptBeforeSampler));
    }
}

public class DocumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("max-dok-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Word_ParagraphsAndTables()
    {
        var path = Path.Combine(_dir, "brief.docx");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
        {
            writer.Write("""
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
                <w:p><w:r><w:t>Sehr geehrte </w:t></w:r><w:r><w:t>Damen und Herren,</w:t></w:r></w:p>
                <w:p><w:r><w:t>die Rechnung liegt bei.</w:t></w:r></w:p>
                <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Posten</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Betrag</w:t></w:r></w:p></w:tc></w:tr>
                <w:tr><w:tc><w:p><w:r><w:t>Miete</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>850 €</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                </w:body></w:document>
                """);
        }

        var result = await new ReadFileTool(() => _dir).RunAsync("brief.docx", CancellationToken.None);

        Assert.Contains("Sehr geehrte Damen und Herren,\ndie Rechnung liegt bei.", result);
        Assert.Contains("Posten | Betrag\nMiete | 850 €", result);
    }

    [Fact]
    public async Task Pdf_TextPerPage()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Kontostand 1234 Euro", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Zweite Seite", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "konto.pdf"), builder.Build());

        var result = await new ReadFileTool(() => _dir).RunAsync("konto.pdf", CancellationToken.None);

        Assert.Contains("PDF mit 2 Seite(n)", result);
        Assert.Contains("--- Seite 1 ---\nKontostand 1234 Euro", result);
        Assert.Contains("--- Seite 2 ---\nZweite Seite", result);
    }

    [Fact]
    public async Task BrokenOrOldFormats_GiveASentence()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "kaputt.pdf"), "kein pdf");
        await File.WriteAllTextAsync(Path.Combine(_dir, "alt.doc"), "x");
        var tool = new ReadFileTool(() => _dir);
        Assert.Contains("ließ sich nicht lesen", await tool.RunAsync("kaputt.pdf", CancellationToken.None));
        Assert.Contains("alten Office-Format", await tool.RunAsync("alt.doc", CancellationToken.None));
    }
}
