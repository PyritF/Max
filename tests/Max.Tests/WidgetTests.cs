using Max.Ui;
using Max.Ui.Markdown;
using Max.Ui.Widgets;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Max.Tests;

public class WidgetTests
{
    private static string Render(string markdown, int width = 80, bool ansi = false)
    {
        var console = new TestConsole().Width(width);
        if (ansi)
            console.Colors(ColorSystem.TrueColor).EmitAnsiSequences();
        console.Write(" ◆ ");
        var writer = new WrapWriter(console, 3);
        var renderer = new MarkdownRenderer(console, writer);
        renderer.Push(markdown);
        renderer.Finish();
        writer.CloseReply();
        return console.Output;
    }

    [Theory]
    [InlineData("balken", "Titel: Test\nA: 3\nB: 5", "█")]
    [InlineData("anteile", "A: 30\nB: 70", "■")]
    [InlineData("kurve", "Mo: 1\nDi: 5\nMi: 2", "┤")]
    [InlineData("fortschritt", "Tests: 50", "50 %")]
    [InlineData("baum", "src/\n  Program.cs\n  Ui/", "└──")]
    [InlineData("kasten", "Titel: Hinweis\nText im Kasten", "Hinweis")]
    [InlineData("spalten", "links\n---\nrechts", "rechts")]
    [InlineData("kalender", "Monat: 2026-10\nMarkiert: 3", "Oktober 2026")]
    [InlineData("titel", "Text: Hi\nSchrift: small", "_")]
    public void Widgets_RenderValidContent(string name, string body, string expected)
    {
        var output = Render($"```{name}\n{body}\n```");
        Assert.Contains(expected, output);
        Assert.DoesNotContain("┌ " + name, output);
    }

    [Theory]
    [InlineData("balken", "keine Zahlen hier")]
    [InlineData("kurve", "Nur: 1")]
    [InlineData("spalten", "nur eine Spalte")]
    [InlineData("balken", "Titel: Status\nStatus: OK")]
    public void BrokenWidget_FallsBackToPlainText(string name, string body)
    {
        var output = Render($"```{name}\n{body}\n```");
        Assert.DoesNotContain(name, output);
        Assert.DoesNotContain("┌", output);
        Assert.DoesNotContain("Titel:", output);
        Assert.Contains(body.Split('\n')[^1], output);
    }

    [Theory]
    [InlineData("Salzbergwerk     Führung: 10", false)]
    [InlineData("Salzbergwerk\tFührung: 10", false)]
    [InlineData("Eine sehr lange Beschreibung als Name einer Zeile: 10", false)]
    [InlineData("Neue Mittelschule: 10", true)]
    public void BarLabels_MustBeShort_AndWithoutColumns(string line, bool valid) =>
        Assert.Equal(valid, Max.Ui.Widgets.WidgetValidator.IsValid("balken", line + "\nB: 5\n"));

    [Fact]
    public void WidgetName_OnTheLineAfterTheFence_IsUnderstood()
    {
        var output = Render("```\nbalken\n-----\nSchlaf: 7 Std.\nArbeit: 8 Std.\n-----\nSumme: 15 Std.\n```");
        Assert.Contains("█", output);
        Assert.DoesNotContain("balken", output);
        Assert.DoesNotContain("Summe", output);
    }

    [Fact]
    public void BareFence_WithCode_StaysACodeBlock()
    {
        var output = Render("```\nvar x = 1;\n```");
        Assert.Contains("┌", output);
        Assert.Contains("var x = 1;", output);
    }

    [Fact]
    public void BrokenTitle_ShowsNothing()
    {
        Assert.Equal("◆", Render("```titel\nSchrift: slant\n```").Trim());
    }

    [Fact]
    public void Widgets_FitNarrowTerminals()
    {
        var output = Render(DemoShowcase(), width: 60);
        foreach (var line in output.Split('\n'))
            Assert.True(line.TrimEnd().Length <= 60, $"Zu breit: '{line}'");
    }

    private static string DemoShowcase() => Max.Commands.DemoCommand.Showcase;

    [Fact]
    public void Tree_UnderstandsDrawnTreeLines()
    {
        // So hat das Modell im Selbsttest geantwortet.
        var output = Render("```baum\nCSharpProjekt/\n├── src/\n│   ├── App/\n│   └── Models/\n├── bin/\n└── Program.cs\n```");
        var lines = output.Split('\n').Select(l => l.TrimEnd()).ToList();
        var app = lines.Single(l => l.Contains("App/"));
        var src = lines.Single(l => l.Contains("src/"));
        Assert.True(app.IndexOf("App/") > src.IndexOf("src/"), "App/ muss unter src/ eingerückt sein.");
        Assert.Contains(lines, l => l.TrimStart().StartsWith("CSharpProjekt/"));
    }

    [Fact]
    public void TitledRule_ShowsTitle()
    {
        var output = Render("--- Abschnitt ---\nText");
        Assert.Contains("Abschnitt", output);
        Assert.Contains("───", output);
        Assert.DoesNotContain("--- Abschnitt", output);
    }

    [Fact]
    public void Code_IsHighlighted_IncludingMultilineComments()
    {
        var output = Render("```csharp\npublic class A { }\n/* eins\nzwei */\n```", ansi: true);
        Assert.Contains("38;2;86;156;214mpublic", output);   // Schlüsselwort blau
        Assert.Contains("38;2;106;153;85mzwei", output);     // Kommentar grün – auch in der zweiten Zeile
    }

    [Fact]
    public void UnknownLanguage_StaysPlain() =>
        Assert.Contains("│ irgendwas", Render("```gibtsnicht\nirgendwas\n```"));

    [Theory]
    [InlineData("12,5", 12.5)]
    [InlineData("12.5", 12.5)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("70 %", 70)]
    [InlineData("3 GB", 3)]
    public void ParseNumber(string text, double expected) =>
        Assert.Equal(expected, WidgetBody.ParseNumber(text));

    [Fact]
    public void AllBuiltInFonts_Load()
    {
        foreach (var font in TitleWidget.BuiltInFonts.Where(f => f != "standard"))
            Assert.NotNull(TitleWidget.LoadFont(font));
        Assert.Null(TitleWidget.LoadFont("gibtsnicht"));
    }

    [Fact]
    public void BrailleCanvas_DrawsLine()
    {
        var canvas = new BrailleCanvas(2, 1);
        canvas.Line((0, 0), (3, 3));
        var line = Assert.Single(canvas.Render());
        Assert.Equal(2, line.Length);
        Assert.All(line, c => Assert.NotEqual('⠀', c));
    }
}

public class InlineExtrasTests
{
    private static List<(string Text, Style Style)> Run(string text)
    {
        var parts = new List<(string, Style)>();
        var formatter = new InlineFormatter((t, s) => parts.Add((t, s)));
        formatter.StartLine(Style.Plain);
        foreach (var c in text)
            formatter.Push(c.ToString()); // Zeichen für Zeichen, wie beim Streamen
        formatter.EndLine();
        return parts;
    }

    [Fact]
    public void Gradient_BlendsColorsAcrossText()
    {
        var parts = Run("{verlauf}abcd{/verlauf}");
        Assert.Equal("abcd", string.Concat(parts.Select(p => p.Text)));
        Assert.Equal(4, parts.Select(p => p.Style.Foreground).Distinct().Count());
        Assert.Equal(Theme.GradientStart, parts[0].Style.Foreground);
        Assert.Equal(Theme.GradientEnd, parts[^1].Style.Foreground);
    }

    [Fact]
    public void Gradient_WithNamedColors()
    {
        ColorTags.TryGet("blau", out var blue);
        Assert.Equal(blue, Run("{verlauf:blau-cyan}xy{/verlauf}")[0].Style.Foreground);
    }

    [Theory]
    [InlineData("Start :rocket: los", "Start 🚀 los")]
    [InlineData("Hinweis: wichtig", "Hinweis: wichtig")]
    [InlineData("um 12:30:45 Uhr", "um 12:30:45 Uhr")]
    [InlineData("x :gibtsnicht: y", "x :gibtsnicht: y")]
    [InlineData("https://example.com", "https://example.com")]
    public void EmojiShortcodes(string input, string expected) =>
        Assert.Equal(expected, string.Concat(Run(input).Select(p => p.Text)));
}

public class ToleranceTests
{
    [Fact]
    public void TableLikeLines_AreUnderstood()
    {
        var data = new WidgetBody("Zeitpunkt: 0 | Wert: 1\nZeitpunkt: 1 | Wert: 2,7\n| 2 | 7,4 |").Numbers();
        Assert.Equal([("0", 1.0), ("1", 2.7), ("2", 7.4)], data);
    }

    [Theory]
    [InlineData("grün-blau")]
    [InlineData("grün zu blau")]
    [InlineData("von Grün nach Blau")]
    [InlineData("grün → blau")]
    public void GradientSpecs(string spec)
    {
        ColorTags.TryGet("grün", out var green);
        ColorTags.TryGet("blau", out var blue);
        Assert.Equal((green, blue), ChartColors.TryGradient(spec));
    }

    [Fact]
    public void TitleWidget_AcceptsTitelInsteadOfText() =>
        Assert.NotNull(WidgetRegistry.TryRender("titel", "Titel: Hallo\nSchrift: big\nVerlauf: rot-pink", 80));

    [Theory]
    [InlineData("{verlauf:grün-blau}ab{/verlauf}")]
    [InlineData("{verlauf grün-blau}ab{/verlauf}")]
    [InlineData("{verlauf=grün-blau}ab{/verlauf}")]
    public void GradientTag_Variants(string text)
    {
        ColorTags.TryGet("grün", out var green);
        var parts = new List<(string, Style)>();
        var formatter = new InlineFormatter((t, s) => parts.Add((t, s)));
        formatter.StartLine(Style.Plain);
        formatter.Push(text);
        formatter.EndLine();
        Assert.Equal("ab", string.Concat(parts.Select(p => p.Item1)));
        Assert.Equal(green, parts[0].Item2.Foreground);
    }
}
