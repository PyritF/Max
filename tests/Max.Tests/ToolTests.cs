using System.Text;
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
        Assert.Contains("dafür gibt es \"bild\"", await new ReadFileTool(() => _dir).RunAsync("bild.png", CancellationToken.None));
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

    [Theory]
    [InlineData("Was ist 123456789 mal 987654321?", "rechnen")]
    [InlineData("Rechne 48.213 * 12", "rechnen")]
    [InlineData("Was ist 17 hoch 12?", "rechnen")]
    [InlineData("Wer hat die Fußball-Weltmeisterschaft 2022 gewonnen? Schau bitte im Web nach.", "websuche")]
    [InlineData("Such im Internet nach einem Rezept für Linsensuppe.", "websuche")]
    [InlineData("Kannst du im Internet nachschauen, wann der Baumarkt aufmacht?", "websuche")]
    [InlineData("Was ist 3 mal 4?", null)]
    [InlineData("Ruf mich unter 0711-456780 an.", null)]
    [InlineData("Lies https://example.com/123456/789", null)]
    [InlineData("Der Bildschirm hat 1920x1080 Pixel.", null)]
    [InlineData("Bist du im Internet?", null)]
    [InlineData("Ich habe im Internet nach Rezepten gesucht, aber nichts gefunden.", null)]
    public void RequiredTool_WhenTheQuestionAsksForIt(string request, string? expected)
    {
        using var http = new HttpClient();
        Assert.Equal(expected, ToolBox.CreateDefault(http, () => DateTime.Now, () => ".").RequiredTool(request));
    }

    [Theory]
    [InlineData("websuche: Wien", true)]
    [InlineData("Rechnen: 2+3", true)]
    [InlineData("uhrzeit", true)]
    [InlineData("system: linux", false)]     // YAML, kein Aufruf: system hat keine Angabe
    [InlineData("rechnen:", false)]
    [InlineData("print(1)", false)]
    [InlineData("zaubern: los", false)]
    public void IsCallLine_KnownNameAndFittingArgument(string line, bool expected)
    {
        using var http = new HttpClient();
        Assert.Equal(expected, ToolBox.CreateDefault(http, () => DateTime.Now, () => ".").IsCallLine(line));
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
    // Selbsttest 50: "Websuche" ohne Backticks – geschrieben wurde der Aufruf dann in einem bash-Block.
    [InlineData("Ich sollte Websuche nutzen, um aktuelle Informationen zu erhalten, da sich Preise ändern können.", "websuche")]
    [InlineData("Ich sollte zuerst die aktuellen Zahlen recherchieren. Ich werde eine Websuche durchführen.", "websuche")]
    [InlineData("Der Nutzer fragt nach einem Fakt, den ich nachweislich bestätigen will. Ich suche im Web.", "websuche")]
    [InlineData("Eine Websuche ist hier nicht nötig, das weiß ich.", null)]
    [InlineData("Der Nutzer fragt, ob ich im Internet suchen kann.", null)]
    [InlineData("Der Nutzer sucht im Netz nach Tipps für Rom.", null)]
    [InlineData("Ich schaue im Internet nach.", "websuche")]
    [InlineData("Der Preis kann sich ständig ändern, ich suche im Web.", "websuche")]
    [InlineData("Ich kann im Web nachsehen, wenn er das möchte.", null)]
    // Selbsttest 51: angekündigt, aber nicht als Aufruf erkannt.
    [InlineData("Das ist eine klare Rechnung, die ich mit dem `rechnen`-Werkzeug machen sollte, um genau zu sein.", "rechnen")]
    [InlineData("Ich muss die Wetter-Werkzeugfunktion aufrufen, um aktuelle Informationen zu erhalten.", "wetter")]
    [InlineData("Ich sollte ein Werkzeug aufrufen, um die Antwort zu verifizieren.", ToolBox.AnyTool)]
    [InlineData("Ich sollte das direkt sagen, ohne ein Werkzeug aufzurufen.", null)]
    [InlineData("Der Nutzer fragt, ob ich ein Werkzeug benutzen kann.", null)]
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
    public async Task ToolCall_InACodeBlock_AfterAnAnnouncement_IsRunToo()
    {
        // Selbsttest 50: erst angekündigt, dann der Aufruf in einem bash-Block – wurde als Code gezeigt.
        var (chunks, conversation) = await Run("Ich sehe nach.\n\n", "```bash\n", "rechnen: 2+3\n", "```\n", "Fünf.", null);

        Assert.Single(chunks, c => c.IsTool);
        Assert.DoesNotContain("```", Text(chunks));
        Assert.EndsWith("Fünf.", Text(chunks));
        Assert.Equal("```werkzeug\nrechnen: 2+3\n```", conversation.Messages[1].Content);
    }

    [Fact]
    public async Task TextBeforeACall_DoesNotSpoilTheCache()
    {
        // Selbsttest 58: Erst Text, dann der Aufruf mitten in der Antwort – danach rechnete Max zweimal den ganzen
        // Verlauf neu (der Text stand im Cache, im Verlauf aber der Aufruf), auf der CPU je acht Minuten.
        var model = new FakeModel("Ich rechne nach.\n\n", "```bash\n", "rechnen: 2+3\n", "```\n", "Fünf.", null, "Gern.", null);
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => false, Tools: new ToolBox([new CalculatorTool()])));
        var conversation = new Conversation();
        conversation.AddUser("Was ist 2+3?");

        var reply = new StringBuilder();
        await foreach (var chunk in backend.StreamReplyAsync(conversation, CancellationToken.None))
            if (!chunk.IsTool && !chunk.IsThinking && !chunk.IsStatus)
                reply.Append(chunk.Text);
        Assert.Equal("Ich rechne nach.\n\nFünf.", reply.ToString());
        // Die zweite Runde rechnet nur Aufruf und Ergebnis dazu – die Frage davor steht noch im Cache.
        var template = new ChatMlTemplate();
        var question = template.Message(ChatRole.System, "Du bist Max.") + template.Message(ChatRole.User, "Was ist 2+3?");
        Assert.Equal(model.Tokenize(question).Count, backend.LastRun!.ReusedTokens);

        conversation.AddAssistant(reply.ToString());           // so wie die Anzeige es tut
        conversation.AddUser("Danke");
        await foreach (var _ in backend.StreamReplyAsync(conversation, CancellationToken.None)) { }
        var next = backend.LastRun!;
        Assert.Equal(next.PromptTokens - model.Tokenize(template.Message(ChatRole.User, "Danke") + template.AssistantStart).Count, next.ReusedTokens);
        Assert.Contains("Ich rechne nach.\n\nFünf.", model.Decode(model.LastPromptBeforeSampler));
    }

    [Fact]
    public async Task RealCode_AfterSomeText_IsShownAsCode()
    {
        var (chunks, conversation) = await Run("So geht es:\n\n", "```bash\n", "ls -la\n", "```\n", "Fertig.", null);

        Assert.DoesNotContain(chunks, c => c.IsTool);
        Assert.Equal("So geht es:\n\n```bash\nls -la\n```\nFertig.", Text(chunks));
        Assert.Single(conversation.Messages);
    }

    [Theory]
    [InlineData("Was ist 123456789 mal 987654321?", true)]
    [InlineData("Was ist 3 mal 4?", false)]
    public async Task QuestionThatNeedsATool_StartsWithTheCall(string question, bool forced)
    {
        // Selbsttest 51: "123456789 mal 987654321" ohne rechnen – die Zahl fehlte. Jetzt legt die Grammatik den Aufruf fest.
        var model = new FakeModel("```werkzeug\n", "rechnen: 2+3\n", "```\n", "Fünf.", null);
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => false, Tools: new ToolBox([new CalculatorTool()])));
        var conversation = new Conversation();
        conversation.AddUser(question);
        await foreach (var _ in backend.StreamReplyAsync(conversation, CancellationToken.None)) { }

        Assert.Equal(forced, model.SamplerGrammarTexts[0]!.Contains("root ::= \"```\" w-werkzeug\n", StringComparison.Ordinal));
        Assert.DoesNotContain("root ::= \"```\" w-werkzeug\n", model.SamplerGrammarTexts[^1]!);   // nach dem Ergebnis frei
    }

    [Fact]
    public async Task AfterAToolResult_AQuestionAtTheEnd_IsLeftOut()
    {
        // Selbsttest 57: "Und hast du das Bild selbst gemacht oder ist es ein Testbild?" hinter fast jedem Ergebnis.
        var (chunks, conversation) = await Run("```werkzeug\n", "rechnen: 2+3\n", "```\n", "Das sind **5**.\n\n", "Und rechnest", " du oft so?", null);
        Assert.Equal("Das sind **5**.\n\n", Text(chunks));
        Assert.Equal(ChatRole.Tool, conversation.Messages[^1].Role);
    }

    [Fact]
    public async Task InSmallTalk_ACounterQuestion_Stays()
    {
        var (chunks, _) = await Run("Alles bestens.\n\n", "Und bei dir?", null);
        Assert.Equal("Alles bestens.\n\nUnd bei dir?", Text(chunks));
    }

    [Fact]
    public async Task ClosingOfAnEarlierAnswer_IsNotRepeated()
    {
        var model = new FakeModel("Rot.\n\n", "Und schwarz getrunken? Ich frage mich, ob das hilft.", null);
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => false));
        var conversation = new Conversation();
        conversation.AddUser("Wie hoch ist die Kaution?");
        conversation.AddAssistant("2.380 €.\n\nUnd schwarz getrunken? Ich frage mich, ob das dir hilft, die Zahlen besser zu behalten.");
        conversation.AddUser("Welche Farbe hat der Kreis?");

        var chunks = new List<ReplyChunk>();
        await foreach (var chunk in backend.StreamReplyAsync(conversation, CancellationToken.None))
            chunks.Add(chunk);

        Assert.Equal("Rot.\n\n", string.Concat(chunks.Select(c => c.Text)));
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
    public void TooMuchForTheContext_ToolResultsAreShortened_TheQuestionStays()
    {
        var model = new FakeModel { ContextSize = 4000 };
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => false, Tools: new ToolBox([new CalculatorTool()])));
        var conversation = new Conversation();
        conversation.AddUser("Was steht in den beiden Dateien?");
        conversation.Add(ChatRole.Assistant, "```werkzeug\ndatei: a.txt\n```");
        conversation.Add(ChatRole.Tool, "A" + new string('a', 3000));
        conversation.Add(ChatRole.Assistant, "```werkzeug\ndatei: b.txt\n```");
        conversation.Add(ChatRole.Tool, "B" + new string('b', 1500));

        var prompt = model.Decode(backend.BuildPrompt(conversation.Messages));

        Assert.Contains("Was steht in den beiden Dateien?", prompt);
        Assert.Contains("(gekürzt – für alles reicht der Platz im Gespräch nicht)", prompt);
        Assert.True(prompt.Length < 4000 - LlmBackend.AnswerReserve);
        Assert.Equal(3001, conversation.Messages[2].Content.Length);          // im Verlauf bleibt alles
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

        public Task<string> LookAsync(string imagePath, string question, CancellationToken ct, int maxTokens = VisionEngine.MaxAnswerTokens)
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
        Assert.Equal(new ToolCall("datei", $"{text} | Was steht da?"), calls[1]);
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

public sealed class FindFilesTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("max-finden-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private void Put(string relative, string content = "x")
    {
        var path = Path.Combine(_home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task<string> Find(string argument) =>
        new FindFilesTool(() => _home, () => _home).RunAsync(argument, CancellationToken.None);

    [Fact]
    public async Task ByName_AndByContent_NotInHiddenOrJunkFolders()
    {
        Put("Dokumente/Steuer/Steuererklärung_2025.pdf");
        Put("Dokumente/Steuer/Steuererklärung_2024.pdf");
        Put("Dokumente/Notizen/finanzen.txt", "Einkaufsliste\nDie Steuererklärung 2025 bis Ende Juli abgeben.\nSonst nichts.");
        Put(".versteckt/Steuererklärung_2025.txt");
        Put("node_modules/paket/Steuererklärung_2025.txt");
        Put(".ssh/Steuererklärung_2025");

        var result = await Find("meine Steuererklärung von 2025");

        Assert.Contains("Im Namen:\n- " + Path.Combine(_home, "Dokumente", "Steuer", "Steuererklärung_2025.pdf"), result);
        Assert.DoesNotContain("2024", result);
        Assert.Contains("Im Inhalt:\n- " + Path.Combine(_home, "Dokumente", "Notizen", "finanzen.txt"), result);
        Assert.Contains("„Die Steuererklärung 2025 bis Ende Juli abgeben.“", result);
        Assert.DoesNotContain("versteckt", result);
        Assert.DoesNotContain("node_modules", result);
        Assert.DoesNotContain(".ssh", result);
    }

    [Fact]
    public async Task TypeWords_FilterByExtension()
    {
        Put("Bilder/Urlaub Kroatien.jpg");
        Put("Notizen/Urlaub Kroatien.txt");

        var result = await Find("Fotos vom Urlaub in Kroatien");

        Assert.Contains("Urlaub Kroatien.jpg", result);
        Assert.DoesNotContain("Urlaub Kroatien.txt", result);
        var (terms, types) = FindFilesTool.Split("Fotos Kroatien");
        Assert.Equal(["kroati"], terms);
        Assert.Contains(".heic", types);
    }

    [Fact]
    public async Task InAFolder_NothingFound_AndNoTerms()
    {
        Put("Projekt/readme.md", "Hallo");
        Put("Anderes/hallo.txt", "Hallo");

        Assert.Contains("readme.md", await Find("Hallo | Projekt"));
        Assert.DoesNotContain("hallo.txt", await Find("Hallo | Projekt"));
        Assert.Contains("Nichts gefunden", await Find("Zebrastreifen"));
        Assert.Contains("keine brauchbaren Suchbegriffe", await Find("die das"));
        Assert.Contains("gibt es nicht", await Find("Hallo | Fehlt"));
    }

    [Theory]
    // Selbsttest 50: "finden: mietvertrag | Ordner: ." – gesucht wurde in einem Ordner namens "Ordner: .".
    [InlineData("Hallo | Ordner: Projekt")]
    [InlineData("Hallo | im Ordner Projekt")]
    [InlineData("Hallo | Pfad: \"Projekt\"")]
    public async Task FolderLabel_IsIgnored(string argument)
    {
        Put("Projekt/readme.md", "Hallo");

        Assert.Contains("readme.md", await Find(argument));
        Assert.Equal("Suche Dateien: Hallo (in Projekt)", new FindFilesTool(() => _home).Describe(argument));
    }

    [Fact]
    public async Task CommasInQueryAndPath_StayCommas()
    {
        Put("Fotos, alt/Rechnung, Strom.txt", "x");

        var result = await Find("Rechnung, Strom | Fotos, alt");

        Assert.Contains("Gesucht nach „Rechnung, Strom“ in " + Path.Combine(_home, "Fotos, alt") + " (", result);
        Assert.Contains("Rechnung, Strom.txt", result);
    }

    [Fact]
    public async Task FolderLabel_RealFolderOfThatName_Wins()
    {
        Put("Ordner Projekt/readme.md", "Hallo");

        Assert.Contains("readme.md", await Find("Hallo | Ordner Projekt"));
        Assert.Equal(".", FindFilesTool.Folder("Ordner: ."));
        Assert.Equal("Ordner", FindFilesTool.Folder("Ordner"));
    }
}

public class ClipboardTests
{
    private sealed class FakeClipboard(ClipboardContent content) : IClipboard
    {
        public ClipboardContent Read() => content;
    }

    private sealed class Eyes : IVision
    {
        public List<(string Path, string Question)> Seen { get; } = [];

        public Task<string> LookAsync(string imagePath, string question, CancellationToken ct, int maxTokens = VisionEngine.MaxAnswerTokens)
        {
            Assert.True(File.Exists(imagePath));
            Seen.Add((imagePath, question));
            return Task.FromResult("Ein Fenster mit der Meldung „Datei nicht gefunden“.");
        }
    }

    [Fact]
    public async Task Screenshot_GoesToTheVision_ThenTheFileIsGone()
    {
        var eyes = new Eyes();
        var tool = new ClipboardTool(new FakeClipboard(ClipboardContent.Empty with { Image = [1, 2, 3], ImageExtension = ".png" }), () => eyes);

        var result = await tool.RunAsync("Was ist das für ein Fehler?", CancellationToken.None);

        Assert.Equal("Bild aus der Zwischenablage:\nEin Fenster mit der Meldung „Datei nicht gefunden“.", result);
        var (path, question) = Assert.Single(eyes.Seen);
        Assert.EndsWith(".png", path);
        Assert.False(File.Exists(path));
        Assert.Equal("Was ist das für ein Fehler? Antworte auf Deutsch.", question);
    }

    [Fact]
    public async Task TextFilesAndEmpty()
    {
        static Task<string> Run(ClipboardContent content) =>
            new ClipboardTool(new FakeClipboard(content), () => null).RunAsync("", CancellationToken.None);

        Assert.Equal("Text in der Zwischenablage:\nHallo Welt", await Run(ClipboardContent.Empty with { Text = "Hallo Welt" }));
        Assert.Contains("- C:\\Bilder\\a.png", await Run(ClipboardContent.Empty with { Files = ["C:\\Bilder\\a.png"] }));
        Assert.Equal("Die Zwischenablage ist leer.", await Run(ClipboardContent.Empty));
        Assert.Contains("Bildverständnis ist noch nicht", await Run(ClipboardContent.Empty with { Image = [1], ImageExtension = ".png" }));
    }

    [Fact]
    public async Task OnlyWhenTheUserAsksForIt()
    {
        var box = new ToolBox([new ClipboardTool(new FakeClipboard(ClipboardContent.Empty with { Text = "geheim123" }), () => null)]);
        var call = new ToolCall("zwischenablage", "");

        Assert.Contains("Nicht ausgeführt", await box.RunAsync(call, CancellationToken.None, "Fass die Webseite zusammen."));
        Assert.Contains("Nicht ausgeführt", await box.RunAsync(call, CancellationToken.None));
        Assert.Contains("geheim123", await box.RunAsync(call, CancellationToken.None, "Was steht in meiner Zwischenablage?"));
    }

    [Fact]
    public void MentionedClipboard_IsLookedAt_BeforeTheAnswer()
    {
        using var http = new HttpClient();
        var box = ToolBox.CreateDefault(http, () => DateTime.Now, () => Path.GetTempPath());

        Assert.Equal(new ToolCall("zwischenablage", "Ich hab einen Screenshot gemacht – was ist das für ein Fehler?"),
            Assert.Single(box.AttachmentCalls("Ich hab einen Screenshot gemacht – was ist das für ein Fehler?")));
        Assert.Empty(box.AttachmentCalls("Was kann man in Wien machen?"));
        Assert.Contains("\"zwischenablage\" ( \": \" [^\\n`]{1,300} )?", box.GrammarRule());
        Assert.Contains("`zwischenablage` oder `zwischenablage: <Frage zum Bild darin>`", box.PromptList());
    }

    [Fact]
    public void ClipboardText_EndsAtTheFirstNull()
    {
        Assert.Equal("Hallo", SystemClipboard.UntilNull("Hallo\0\0Rest vom Speicher"));
        Assert.Equal("Hallo", SystemClipboard.UntilNull("Hallo"));
    }

    [Fact]
    public void WindowsBitmap_BecomesABmpFile()
    {
        // 2×2 Pixel, 24 Bit: Kopf (40 Bytes) + zwei Zeilen à 8 Bytes (6 + 2 Füllbytes).
        var dib = new byte[40 + 16];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(2).CopyTo(dib, 4);
        BitConverter.GetBytes(2).CopyTo(dib, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes((ushort)24).CopyTo(dib, 14);

        var bmp = SystemClipboard.DibToBmp(dib);

        Assert.Equal("BM", System.Text.Encoding.ASCII.GetString(bmp, 0, 2));
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
        Assert.Equal(54, BitConverter.ToInt32(bmp, 10));
        Assert.Equal(dib, bmp[14..]);
    }
}
