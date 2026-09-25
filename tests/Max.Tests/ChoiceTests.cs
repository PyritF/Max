using Max.Chat;
using Max.Ui;
using Max.Ui.Markdown;
using Spectre.Console.Testing;

namespace Max.Tests;

public class ChoiceQuestionTests
{
    [Fact]
    public void Frage_und_Antworten_werden_gelesen()
    {
        var q = ChoiceQuestion.TryParse("Frage: Welche Sprache?\n- C#\n- **Python**\n2. {grün}Rust{/grün}\n");

        Assert.NotNull(q);
        Assert.Equal("Welche Sprache?", q.Question);
        Assert.Equal(["C#", "Python", "Rust"], q.Options);
    }

    [Fact]
    public void Frage_ohne_Schluessel_ist_die_erste_Textzeile()
    {
        var q = ChoiceQuestion.TryParse("Wie weiter?\nAntworten:\n* Ja\n* Nein");

        Assert.Equal("Wie weiter?", q!.Question);
        Assert.Equal(["Ja", "Nein"], q.Options);
    }

    [Fact]
    public void Leerer_Block_ist_keine_Frage()
    {
        Assert.Null(ChoiceQuestion.TryParse("\n  \n"));
    }
}

public class ChoiceMenuStateTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new(key == ConsoleKey.Enter ? '\r' : '\0', key, false, false, false);
    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.A, false, false, false);

    [Fact]
    public void Enter_waehlt_die_markierte_Antwort()
    {
        var state = new ChoiceMenuState(["A", "B", "C"]);

        state.Handle(Key(ConsoleKey.DownArrow));
        var outcome = state.Handle(Key(ConsoleKey.Enter));

        Assert.Equal(ChoiceMenuState.Outcome.Submit, outcome);
        Assert.Equal("B", state.Answer);
    }

    [Fact]
    public void Pfeil_hoch_springt_von_oben_zur_Freitext_Zeile()
    {
        var state = new ChoiceMenuState(["A", "B"]);

        state.Handle(Key(ConsoleKey.UpArrow));

        Assert.True(state.OnFreeText);
    }

    [Fact]
    public void Ziffer_waehlt_direkt()
    {
        var state = new ChoiceMenuState(["A", "B", "C"]);

        Assert.Equal(ChoiceMenuState.Outcome.Submit, state.Handle(Char('3')));
        Assert.Equal("C", state.Answer);
    }

    [Fact]
    public void Tippen_landet_in_der_Freitext_Zeile()
    {
        var state = new ChoiceMenuState(["A", "B"]);

        foreach (var c in "Mal 2")
            state.Handle(Char(c));
        state.Handle(Key(ConsoleKey.Backspace));
        state.Handle(Char('3'));
        var outcome = state.Handle(Key(ConsoleKey.Enter));

        Assert.Equal(ChoiceMenuState.Outcome.Submit, outcome);
        Assert.Equal("Mal 3", state.Answer);
    }

    [Fact]
    public void Leerer_Freitext_wird_nicht_gesendet()
    {
        var state = new ChoiceMenuState(["A"]);
        state.Handle(Key(ConsoleKey.DownArrow));

        Assert.Equal(ChoiceMenuState.Outcome.Continue, state.Handle(Key(ConsoleKey.Enter)));
    }

    [Fact]
    public void Esc_bricht_ab()
    {
        Assert.Equal(ChoiceMenuState.Outcome.Cancel, new ChoiceMenuState(["A"]).Handle(Key(ConsoleKey.Escape)));
    }

    [Fact]
    public void Lange_Antworten_werden_gekuerzt()
    {
        Assert.Equal("abcd…", ChoiceMenu.Fit("abcdefgh", 5));
        Assert.Equal("…fgh", ChoiceMenu.FitEnd("abcdefgh", 4));
    }
}

public class QuestionRenderingTests
{
    private const string Reply = "Klingt gut.\n\n```frage\nFrage: Wie weiter?\n- Kurz\n- Ausführlich\n```\n";

    [Fact]
    public async Task Ohne_Menue_stehen_die_Antworten_als_Liste_im_Text()
    {
        var console = new TestConsole().Width(60);
        var view = new ChatView(console, animate: false);

        await view.StreamReplyAsync(Stuecke(Reply), CancellationToken.None);

        Assert.Contains("Wie weiter?", console.Output);
        Assert.Contains("1. Kurz", console.Output);
        Assert.DoesNotContain("```", console.Output);
        Assert.Equal(["Kurz", "Ausführlich"], view.LastQuestion!.Options);
    }

    [Fact]
    public void Mit_Menue_steht_nur_die_Frage_im_Text()
    {
        var console = new TestConsole().Width(60);
        var writer = new WrapWriter(console, 3);
        var renderer = new MarkdownRenderer(console, writer, listQuestionOptions: false);

        renderer.Push(Reply);
        renderer.Finish();

        Assert.Contains("Wie weiter?", console.Output);
        Assert.DoesNotContain("Kurz", console.Output);
        Assert.NotNull(renderer.Question);
    }

    private static async IAsyncEnumerable<ReplyChunk> Stuecke(string text)
    {
        foreach (var c in text.Chunk(7))
        {
            await Task.Yield();
            yield return new ReplyChunk(new string(c));
        }
    }
}
