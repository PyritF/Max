using Max.Ui;

namespace Max.Tests;

public class ColorTagsTests
{
    private static List<ColoredText> Run(params string[] chunks)
    {
        var tags = new ColorTags();
        var result = chunks.SelectMany(tags.Push).ToList();
        result.AddRange(tags.Flush());
        return result;
    }

    private static string Plain(IEnumerable<ColoredText> parts) => string.Concat(parts.Select(p => p.Text));

    [Fact]
    public void KnownColor_IsApplied()
    {
        var parts = Run("vor {grün}fertig{/grün} nach");
        Assert.Equal("vor fertig nach", Plain(parts));
        Assert.Null(parts[0].Color);
        Assert.NotNull(parts.Single(p => p.Text == "fertig").Color);
        Assert.Null(parts[^1].Color);
    }

    [Fact]
    public void Tag_SplitAcrossChunks() =>
        Assert.Equal("Achtung!", Plain(Run("{", "ro", "t}Acht", "ung!{/r", "ot}")));

    [Fact]
    public void UnknownTag_StaysText() => Assert.Equal("{name} bleibt", Plain(Run("{name} bleibt")));

    [Theory]
    [InlineData("arr[0] = x[1];")]
    [InlineData("if (a) { b(); }")]
    [InlineData("f\"{wert:.2f}\"")]
    public void CodeLikeText_IsUntouched(string code) => Assert.Equal(code, Plain(Run(code)));

    [Fact]
    public void UnclosedTag_EndsWithReply()
    {
        var parts = Run("{rot}bis zum Ende");
        Assert.Equal("bis zum Ende", Plain(parts));
        Assert.All(parts, p => Assert.NotNull(p.Color));
    }

    [Fact]
    public void StrayClosingTag_StaysText() => Assert.Equal("x{/rot}", Plain(Run("x{/rot}")));

    [Fact]
    public void UnfinishedBrace_IsFlushedAtEnd() => Assert.Equal("Menge {", Plain(Run("Menge {")));
}
