using Spectre.Console;
using Max.Chat;
using Max.Ui;
using Spectre.Console.Testing;

namespace Max.Tests;

public class ConversationTests
{
    [Fact]
    public void Nachrichten_werden_in_Reihenfolge_gespeichert()
    {
        var conversation = new Conversation();

        conversation.AddUser("frage");
        conversation.AddAssistant("antwort");

        Assert.Collection(conversation.Messages,
            m => Assert.Equal((ChatRole.User, "frage"), (m.Role, m.Content)),
            m => Assert.Equal((ChatRole.Assistant, "antwort"), (m.Role, m.Content)));
        Assert.Equal("frage", conversation.LastUserMessage?.Content);
    }

    [Fact]
    public void Clear_leert_alles()
    {
        var conversation = new Conversation();
        conversation.AddUser("frage");

        conversation.Clear();

        Assert.Empty(conversation.Messages);
        Assert.Null(conversation.LastUserMessage);
    }
}

public class PlaceholderBackendTests
{
    private static PlaceholderBackend Backend() =>
        new(TimeSpan.Zero, TimeSpan.Zero, new ErsteVorlage());

    [Fact]
    public async Task Antwort_greift_die_Frage_auf()
    {
        var conversation = new Conversation();
        conversation.AddUser("Wie spät ist es?");

        var chunks = new List<string>();
        await foreach (var chunk in Backend().StreamReplyAsync(conversation, CancellationToken.None))
            chunks.Add(chunk);

        Assert.True(chunks.Count > 1, "Die Antwort soll in mehreren Stücken kommen.");
        Assert.Contains("Wie spät ist es?", string.Concat(chunks));
    }

    [Fact]
    public async Task Abbruch_beendet_das_Streaming()
    {
        var conversation = new Conversation();
        conversation.AddUser("hallo");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Backend().StreamReplyAsync(conversation, cts.Token)) { }
        });
    }

    /// <summary>Wählt immer die erste Vorlage – die mit der zitierten Frage.</summary>
    private sealed class ErsteVorlage : Random
    {
        public override int Next(int maxValue) => 0;
    }
}

public class ChatViewTests
{
    [Fact]
    public async Task Gestreamte_Stuecke_erscheinen_vollstaendig()
    {
        var console = new TestConsole();
        var view = new ChatView(console, animate: false);

        var text = await view.StreamReplyAsync(Stuecke("Hallo ", "Welt"), CancellationToken.None);

        Assert.Equal("Hallo Welt", text);
        Assert.Contains("Hallo Welt", console.Output);
    }

    [Fact]
    public async Task Abbruch_wird_angezeigt_und_der_bisherige_Text_behalten()
    {
        var console = new TestConsole();
        var view = new ChatView(console, animate: false);
        using var cts = new CancellationTokenSource();

        var text = await view.StreamReplyAsync(StueckeMitAbbruch(cts), cts.Token);

        Assert.Equal("Erster Teil ", text);
        Assert.Contains("(abgebrochen)", console.Output);
    }

    [Fact]
    public async Task Lange_Antworten_werden_eingerueckt_umgebrochen()
    {
        var console = new TestConsole().Width(30);
        var view = new ChatView(console, animate: false);

        await view.StreamReplyAsync(Stuecke("eins ", "zwei ", "drei ", "vier ", "fünf ", "sechs ", "sieben"), CancellationToken.None);

        var zeilen = console.Lines.Where(l => l.Trim().Length > 0).ToList();
        Assert.True(zeilen.Count >= 2, "Die Antwort soll umgebrochen werden.");
        Assert.All(zeilen, l => Assert.True(l.TrimEnd().Length < 30));
        Assert.StartsWith("   ", zeilen[1]);
    }

    [Fact]
    public async Task Woerter_aus_mehreren_Stuecken_werden_nie_zerrissen()
    {
        var console = new TestConsole().Width(24);
        var view = new ChatView(console, animate: false);

        // "Präzision" kommt in drei Stücken und passt nicht mehr in die erste Zeile.
        await view.StreamReplyAsync(Stuecke("Das ist eine ", "Präz", "isi", "on beim Umbruch."), CancellationToken.None);

        var zeilen = console.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        Assert.Contains(zeilen, l => l.Contains("Präzision"));
        Assert.All(console.Lines, l => Assert.True(l.TrimEnd().Length < 24, $"Zeile zu lang: '{l}'"));
    }

    [Fact]
    public async Task Ueberlange_Woerter_werden_hart_geteilt()
    {
        var console = new TestConsole().Width(20);
        var view = new ChatView(console, animate: false);

        await view.StreamReplyAsync(Stuecke("https://example.com/ein/sehr/langer/pfad"), CancellationToken.None);

        Assert.All(console.Lines, l => Assert.True(l.TrimEnd().Length < 20, $"Zeile zu lang: '{l}'"));
        Assert.Equal("https://example.com/ein/sehr/langer/pfad", string.Concat(console.Lines.Select(l => l.Replace("◆", "").Trim())));
    }

    [Fact]
    public async Task Emojis_zaehlen_zwei_Spalten()
    {
        var console = new TestConsole().Width(12);
        var view = new ChatView(console, animate: false);

        // " ◆ " (3) + "👍👍👍👍" (8) = 11 → passt nicht mehr hinter die 11. Spalte.
        await view.StreamReplyAsync(Stuecke("👍👍👍👍 ok"), CancellationToken.None);

        var zeilen = console.Lines.Where(l => l.Trim().Length > 0).ToList();
        Assert.Equal(2, zeilen.Count);
        Assert.Equal("ok", zeilen[1].Trim());
    }

    [Fact]
    public async Task Farb_Tags_werden_angewendet_und_bleiben_im_Verlauf()
    {
        var console = new TestConsole().Colors(ColorSystem.TrueColor).EmitAnsiSequences();
        var view = new ChatView(console, animate: false);

        var text = await view.StreamReplyAsync(Stuecke("Das ist {r", "ot}wichtig{/rot}."), CancellationToken.None);

        Assert.Equal("Das ist {rot}wichtig{/rot}.", text);
        Assert.DoesNotContain("{rot}", console.Output);
        Assert.Contains("\u001b[38;2;240;80;80mwichtig", console.Output);
    }

    [Fact]
    public async Task Bei_Abbruch_wird_das_angefangene_Wort_noch_ausgegeben()
    {
        var console = new TestConsole();
        var view = new ChatView(console, animate: false);
        using var cts = new CancellationTokenSource();

        await view.StreamReplyAsync(StueckeMitAbbruch(cts, "Halbes Wo"), cts.Token);

        Assert.Contains("Halbes Wo (abgebrochen)", console.Output);
    }

    [Fact]
    public async Task Cursor_ist_waehrend_der_Antwort_versteckt_und_danach_wieder_da()
    {
        var console = new TestConsole().EmitAnsiSequences();
        var view = new ChatView(console, animate: true);

        await view.StreamReplyAsync(Stuecke("Hallo"), CancellationToken.None);

        var hide = console.Output.IndexOf("\u001b[?25l", StringComparison.Ordinal);
        var show = console.Output.LastIndexOf("\u001b[?25h", StringComparison.Ordinal);
        Assert.True(hide >= 0 && show > hide, "Cursor soll erst versteckt und am Ende wieder gezeigt werden.");
    }

    private static async IAsyncEnumerable<string> Stuecke(params string[] teile)
    {
        foreach (var teil in teile)
        {
            await Task.Yield();
            yield return teil;
        }
    }

    private static async IAsyncEnumerable<string> StueckeMitAbbruch(CancellationTokenSource cts, string erster = "Erster Teil ")
    {
        yield return erster;
        await cts.CancelAsync();
        cts.Token.ThrowIfCancellationRequested();
        yield return "kommt nie an";
    }
}

public class CtrlCPolicyTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void Erster_Druck_zeigt_Hinweis()
    {
        Assert.Equal(CtrlCPolicy.Action.ShowHint, new CtrlCPolicy().Press(Start));
    }

    [Fact]
    public void Zweiter_Druck_kurz_danach_beendet()
    {
        var policy = new CtrlCPolicy();
        policy.Press(Start);

        Assert.Equal(CtrlCPolicy.Action.Exit, policy.Press(Start.AddSeconds(1)));
    }

    [Fact]
    public void Zweiter_Druck_zu_spaet_zeigt_wieder_Hinweis()
    {
        var policy = new CtrlCPolicy();
        policy.Press(Start);

        Assert.Equal(CtrlCPolicy.Action.ShowHint, policy.Press(Start.AddSeconds(3)));
    }
}
