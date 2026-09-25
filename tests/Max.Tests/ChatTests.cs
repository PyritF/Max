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

    private static async IAsyncEnumerable<string> Stuecke(params string[] teile)
    {
        foreach (var teil in teile)
        {
            await Task.Yield();
            yield return teil;
        }
    }

    private static async IAsyncEnumerable<string> StueckeMitAbbruch(CancellationTokenSource cts)
    {
        yield return "Erster Teil ";
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
