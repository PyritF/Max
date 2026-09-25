using Max.Ui;

namespace Max.Tests;

public class LineEditorTests
{
    private static LineEditor NewEditor(InputHistory? history = null) =>
        new(history ?? new InputHistory(null), () => ["help", "clear", "exit", "debug"]);

    private static ConsoleKeyInfo Key(char c) => new(c, CharToKey(c), false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey key, bool shift = false, bool alt = false, bool ctrl = false) =>
        new(key == ConsoleKey.Enter ? '\r' : '\0', key, shift, alt, ctrl);

    private static ConsoleKey CharToKey(char c) => c switch
    {
        '\r' or '\n' => ConsoleKey.Enter,
        '\t' => ConsoleKey.Tab,
        _ => char.IsLetter(c) ? (ConsoleKey)char.ToUpperInvariant(c) : ConsoleKey.Oem1,
    };

    private static LineEditor.Outcome Type(LineEditor editor, string text, bool asPaste = false)
    {
        var outcome = LineEditor.Outcome.Continue;
        for (var i = 0; i < text.Length; i++)
            outcome = editor.Handle(Key(text[i]), moreKeysPending: asPaste && i < text.Length - 1);
        return outcome;
    }

    [Fact]
    public void Enter_Submits()
    {
        var editor = NewEditor();
        Type(editor, "hallo");
        Assert.Equal(LineEditor.Outcome.Submit, editor.Handle(Key(ConsoleKey.Enter), false));
        Assert.Equal("hallo", editor.Text);
    }

    [Fact]
    public void PastedText_WithNewlines_StaysOneInput()
    {
        var editor = NewEditor();
        var outcome = Type(editor, "Zeile eins\rZeile zwei\rZeile drei\r", asPaste: true);

        Assert.Equal(LineEditor.Outcome.Continue, outcome); // auch das letzte Enter gehört noch zum Einfügen
        Assert.Equal("Zeile eins\nZeile zwei\nZeile drei\n", editor.Text);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ShiftOrAltEnter_InsertsNewline(bool shift, bool alt)
    {
        var editor = NewEditor();
        Type(editor, "a");
        Assert.Equal(LineEditor.Outcome.Continue, editor.Handle(Key(ConsoleKey.Enter, shift, alt), false));
        Type(editor, "b");
        Assert.Equal("a\nb", editor.Text);
    }

    [Fact]
    public void BackslashEnter_InsertsNewline_WithoutBackslash()
    {
        var editor = NewEditor();
        Type(editor, "a\\");
        Assert.Equal(LineEditor.Outcome.Continue, editor.Handle(Key(ConsoleKey.Enter), false));
        Assert.Equal("a\n", editor.Text);
    }

    [Fact]
    public void CursorMovement_WordJumps_AndDeleting()
    {
        var editor = NewEditor();
        Type(editor, "eins zwei drei");
        editor.Handle(Key(ConsoleKey.LeftArrow, ctrl: true), false);
        Assert.Equal(10, editor.Cursor);                     // vor "drei"
        editor.Handle(Key(ConsoleKey.Backspace, ctrl: true), false);
        Assert.Equal("eins drei", editor.Text);             // "zwei " gelöscht
        editor.Handle(Key(ConsoleKey.Home), false);
        editor.Handle(Key(ConsoleKey.Delete), false);
        Assert.Equal("ins drei", editor.Text);
        editor.Handle(Key(ConsoleKey.End), false);
        editor.Handle(Key(ConsoleKey.Backspace), false);
        Assert.Equal("ins dre", editor.Text);
    }

    [Fact]
    public void Emoji_IsDeletedAsOne()
    {
        var editor = NewEditor();
        editor.SetText("ok 👍");
        editor.Handle(Key(ConsoleKey.Backspace), false);
        Assert.Equal("ok ", editor.Text);
    }

    [Fact]
    public void UpDown_MoveBetweenLines_ThenThroughHistory()
    {
        var history = new InputHistory(null);
        history.Add("alt");
        var editor = NewEditor(history);
        editor.SetText("zeile1\nzeile2");

        editor.Handle(Key(ConsoleKey.UpArrow), false);
        Assert.Equal("zeile1\nzeile2", editor.Text);       // erst innerhalb des Textes nach oben
        Assert.True(editor.Cursor <= 6);

        editor.Handle(Key(ConsoleKey.UpArrow), false);
        Assert.Equal("alt", editor.Text);                   // dann durch den Verlauf

        editor.Handle(Key(ConsoleKey.DownArrow), false);
        Assert.Equal("zeile1\nzeile2", editor.Text);       // und zurück zum Entwurf
    }

    [Fact]
    public void Tab_CompletesCommands()
    {
        var editor = NewEditor();
        Type(editor, "/he");
        editor.Handle(Key(ConsoleKey.Tab), false);
        Assert.Equal("/help ", editor.Text);

        editor.SetText("/");
        editor.Handle(Key(ConsoleKey.Tab), false);
        Assert.NotNull(editor.Hint);                        // mehrere Treffer → Liste im Hinweis
    }

    [Fact]
    public void Escape_Clears() 
    {
        var editor = NewEditor();
        Type(editor, "weg damit");
        editor.Handle(Key(ConsoleKey.Escape), false);
        Assert.Equal("", editor.Text);
    }
}

public sealed class InputHistoryTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "max-history-" + Guid.NewGuid().ToString("N") + ".txt");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void SavesAndLoads_MultilineEntries()
    {
        var history = new InputHistory(_file);
        history.Add("eins");
        history.Add("zwei\ndrei mit \\ Backslash");

        var reloaded = new InputHistory(_file);
        Assert.Equal(["eins", "zwei\ndrei mit \\ Backslash"], reloaded.Entries);
    }

    [Fact]
    public void Duplicates_MoveToTheEnd_AndLimitApplies()
    {
        var history = new InputHistory(_file, limit: 3);
        foreach (var e in new[] { "a", "b", "a", "c", "d" })
            history.Add(e);
        Assert.Equal(["a", "c", "d"], history.Entries);
    }
}

public class InputBoxLayoutTests
{
    [Fact]
    public void LongLines_Wrap_AndCursorIsFound()
    {
        var layout = InputBox.Layout("abcdefghij\nxy", cursor: 12, width: 4);
        Assert.Equal(["abcd", "efgh", "ij", "xy"], layout.Rows.Select(r => r.Text));
        Assert.True(layout.Rows[0].IsFirstOfText);
        Assert.Equal(3, layout.CursorRow);
        Assert.Equal(1, layout.CursorColumn);
        Assert.Equal(2, layout.LogicalLines);
    }

    [Fact]
    public void Emoji_CountsTwoCells()
    {
        var layout = InputBox.Layout("👍👍👍", cursor: 6, width: 4);
        Assert.Equal(2, layout.Rows.Count);
        Assert.Equal(2, layout.CursorColumn);
    }
}
