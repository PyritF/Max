using System.Runtime.CompilerServices;

namespace Max.Chat;

/// <summary>
/// Antwortet, solange es noch kein Sprachmodell gibt: kurze Denkpause, dann eine
/// Antwort im Ton von Max, Wort für Wort – genau wie später das echte Modell.
/// </summary>
internal sealed class PlaceholderBackend(TimeSpan thinkDelay, TimeSpan wordDelay, Random random) : IChatBackend
{
    private const int MaxQuoteLength = 60;

    private static readonly string[] Templates =
    [
        "Mein Verstand wird gerade erst installiert. Bis dahin nur so viel: Du sagtest „{0}“.",
        "„{0}“ – notiert. Sobald ich denken kann, komme ich darauf zurück.",
        "Eine berechtigte Frage. Leider fehlt mir noch das Gehirn, um sie zu beantworten. Es ist unterwegs.",
        "Ich höre dich. Verstehen kommt in einer der nächsten Versionen.",
    ];

    public PlaceholderBackend()
        : this(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(45), Random.Shared)
    {
    }

    public async IAsyncEnumerable<ReplyChunk> StreamReplyAsync(
        Conversation conversation,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(thinkDelay, ct);

        var question = conversation.LastUserMessage?.Content.Trim() ?? "";
        var reply = string.Format(Templates[random.Next(Templates.Length)], Shorten(question));

        var words = reply.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ReplyChunk(i < words.Length - 1 ? words[i] + " " : words[i]);
            await Task.Delay(wordDelay, ct);
        }
    }

    private static string Shorten(string text) =>
        text.Length <= MaxQuoteLength ? text : text[..(MaxQuoteLength - 1)] + "…";
}
