using Max.Ui;
using Max.Ui.Markdown;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Max.Tests;

public class InlineFormatterTests
{
    private static List<(string Text, Style Style)> Run(params string[] chunks)
    {
        var parts = new List<(string, Style)>();
        var formatter = new InlineFormatter((text, style) => parts.Add((text, style)));
        formatter.StartLine(Style.Plain);
        foreach (var chunk in chunks)
            formatter.Push(chunk);
        formatter.EndLine();
        return parts;
    }

    private static string Plain(IEnumerable<(string Text, Style Style)> parts) => string.Concat(parts.Select(p => p.Text));

    private static string Styled(IEnumerable<(string Text, Style Style)> parts, Func<Style, bool> predicate) =>
        string.Concat(parts.Where(p => predicate(p.Style)).Select(p => p.Text));

    [Fact]
    public void SquareBracketColorTags_AreUnderstoodToo_OtherBracketsStay()
    {
        var parts = Run("ein [rot]roter[/rot] Text, [Link](x) und arr[0]");
        Assert.Equal("ein roter Text, [Link](x) und arr[0]", Plain(parts));
        Assert.Equal("roter", Styled(parts, s => s.Foreground != Style.Plain.Foreground));
    }

    [Fact]
    public void Bold_Italic_Code()
    {
        var parts = Run("ein **fettes** und *schräges* mit `code`");
        Assert.Equal("ein fettes und schräges mit code", Plain(parts));
        Assert.Equal("fettes", Styled(parts, s => s.Decoration.HasFlag(Decoration.Bold)));
        Assert.Equal("schräges", Styled(parts, s => s.Decoration.HasFlag(Decoration.Italic)));
        Assert.Equal("code", Styled(parts, s => s.Foreground == InlineFormatter.CodeColor));
    }

    [Fact]
    public void Markers_SplitAcrossChunks() =>
        Assert.Equal("fett", Styled(Run("**f", "et", "t*", "*"), s => s.Decoration.HasFlag(Decoration.Bold)));

    [Theory]
    [InlineData("3 * 4 = 12")]
    [InlineData("snake_case und a*b")]
    [InlineData("arr[0] = x[1];")]
    public void MathAndCode_StayLiteral(string text) => Assert.Equal(text, Plain(Run(text)));

    [Fact]
    public void CodeSpan_IgnoresMarkdownInside() =>
        Assert.Equal("a **b** c", Styled(Run("`a **b** c`"), s => s.Foreground == InlineFormatter.CodeColor));

    [Fact]
    public void KnownColor_IsApplied()
    {
        ColorTags.TryGet("grün", out var green);
        var parts = Run("vor {grün}fertig{/grün} nach");
        Assert.Equal("vor fertig nach", Plain(parts));
        Assert.Equal("fertig", Styled(parts, s => s.Foreground == green));
    }

    [Fact]
    public void ColorTag_SplitAcrossChunks() => Assert.Equal("Achtung!", Plain(Run("{", "ro", "t}Acht", "ung!{/r", "ot}")));

    [Theory]
    [InlineData("{name} bleibt")]
    [InlineData("if (a) { b(); }")]
    [InlineData("x{/rot}")]
    [InlineData("Menge {")]
    public void UnknownOrBrokenTags_StayText(string text) => Assert.Equal(text, Plain(Run(text)));

    [Fact]
    public void Emoji_StaysInOnePiece() =>
        Assert.Contains(Run("ok 👍 gut"), p => p.Text.Contains("👍"));

    [Fact]
    public void ToMarkup_EscapesBrackets() =>
        Assert.Contains("[[0]]", InlineFormatter.ToMarkup("arr[0]", Style.Plain));
}

public class MarkdownRendererTests
{
    private static TestConsoleOutput Render(int width, params string[] chunks)
    {
        var console = new TestConsole().Width(width);
        console.Write(" ◆ "); // wie in ChatView: der Text beginnt hinter dem Symbol
        var writer = new WrapWriter(console, 3);
        var renderer = new MarkdownRenderer(console, writer);
        foreach (var chunk in chunks)
            renderer.Push(chunk);
        renderer.Finish();
        writer.CloseReply();
        return new TestConsoleOutput(console.Lines.Select(l => l.TrimEnd()).ToList(), console.Output);
    }

    internal sealed record TestConsoleOutput(List<string> Lines, string Raw)
    {
        public string Text => string.Join("\n", Lines);
    }

    [Fact]
    public void Heading_And_List()
    {
        var output = Render(60, "# Titel\n\n- eins\n", "- zw", "ei\n1. erster\n");
        Assert.Contains(" ◆ Titel", output.Lines);
        Assert.Contains("   • eins", output.Lines);
        Assert.Contains("   • zwei", output.Lines);
        Assert.Contains("   1. erster", output.Lines);
        Assert.DoesNotContain("#", output.Text);
    }

    [Fact]
    public void ListItems_WrapWithHangingIndent()
    {
        var output = Render(30, "- ein sehr langer Listenpunkt der umbrechen muss\n");
        Assert.StartsWith(" ◆ • ein", output.Lines[0]);
        Assert.StartsWith("     ", output.Lines[1]);
        Assert.All(output.Lines, l => Assert.True(l.Length < 30, l));
    }

    [Fact]
    public void CodeBlock_HasFrame_AndKeepsIndentation()
    {
        var output = Render(60, "Code:\n```py", "thon\ndef f():\n    return 1\n```\nfertig");
        Assert.Contains("   ┌ python", output.Lines);
        Assert.Contains("   │ def f():", output.Lines);
        Assert.Contains("   │     return 1", output.Lines);
        Assert.Contains("   └", output.Lines);
        Assert.Contains("   fertig", output.Lines);
        Assert.DoesNotContain("```", output.Text);
    }

    [Fact]
    public void MarkdownFence_IsRenderedNotShownAsCode()
    {
        var output = Render(60, "```markdown\n# Die Maus\n**fett**\n```\nEnde");
        Assert.Contains(" ◆ Die Maus", output.Lines);
        Assert.Contains("   fett", output.Lines);
        Assert.DoesNotContain("┌", output.Text);
        Assert.DoesNotContain("```", output.Text);
    }

    [Fact]
    public void Table_IsDrawnWithFrame()
    {
        var output = Render(60, "| ID | Status |\n|---|---|\n| 1 | ✅ ok |\n| 2 | offen |\n\nDanach.");
        Assert.Contains(output.Lines, l => l.Contains("╭"));
        Assert.Contains(output.Lines, l => l.Contains("Status"));
        Assert.Contains(output.Lines, l => l.Contains("offen"));
        Assert.DoesNotContain(output.Lines, l => l.Contains("---"));
        Assert.Contains("   Danach.", output.Lines);
    }

    [Fact]
    public void Rule_And_Quote()
    {
        var output = Render(40, "oben\n---\n> weise Worte\n");
        Assert.Contains(output.Lines, l => l.TrimStart().StartsWith("───"));
        Assert.Contains("   │ weise Worte", output.Lines);
    }

    [Fact]
    public void BlankLines_Collapse()
    {
        var output = Render(40, "a\n\n\n\nb");
        Assert.Equal([" ◆ a", "", "   b"], output.Lines.Take(3));
    }

    [Fact]
    public void BoldAtLineStart_IsNotAList() =>
        Assert.Contains(" ◆ Wichtig: ja", Render(40, "**Wichtig**: ja").Lines);
}
