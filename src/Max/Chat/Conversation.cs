namespace Max.Chat;

/// <summary>
/// Der Gesprächsverlauf. Wird er zu lang für den Kontext, notiert sich Max den Anfang (<see cref="Summary"/>) –
/// die Nachrichten davor gehen dann nicht mehr ins Modell, stehen aber weiter hier (und im gespeicherten Gespräch).
/// </summary>
internal sealed class Conversation
{
    private readonly List<ChatMessage> _messages = [];

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Die letzte Nachricht des Nutzers, falls es eine gibt.</summary>
    public ChatMessage? LastUserMessage => _messages.LastOrDefault(m => m.Role == ChatRole.User);

    /// <summary>Unter diesem Namen ist das Gespräch gespeichert (siehe <see cref="ConversationArchive"/>) – null, solange noch nicht.</summary>
    public string? Id { get; set; }

    /// <summary>Was Max sich vom Anfang notiert hat, der nicht mehr in den Kontext passt.</summary>
    public string? Summary { get; private set; }

    /// <summary>So viele Nachrichten vom Anfang deckt <see cref="Summary"/> ab – sie gehen nicht mehr ins Modell.</summary>
    public int SummarizedCount { get; private set; }

    public void Add(ChatRole role, string content) =>
        _messages.Add(new ChatMessage(role, content, DateTime.Now));

    public void AddUser(string content) => Add(ChatRole.User, content);

    public void AddAssistant(string content) => Add(ChatRole.Assistant, content);

    public void Summarize(string summary, int count)
    {
        Summary = summary;
        SummarizedCount = Math.Clamp(count, 0, _messages.Count);
    }

    /// <summary>Ein neues Gespräch: alles weg, auch die Notiz und der Name.</summary>
    public void Clear()
    {
        _messages.Clear();
        Summary = null;
        SummarizedCount = 0;
        Id = null;
    }

    /// <summary>Ein gespeichertes Gespräch fortsetzen – es ersetzt das laufende.</summary>
    public void Load(string id, IEnumerable<ChatMessage> messages, string? summary, int summarizedCount)
    {
        Clear();
        _messages.AddRange(messages);
        Id = id;
        if (summary is not null)
            Summarize(summary, summarizedCount);
    }
}
