namespace Max.Chat;

/// <summary>
/// Der Gesprächsverlauf. Später kommen hier der System-Prompt und das Kürzen
/// auf die Kontextlänge des Modells dazu.
/// </summary>
internal sealed class Conversation
{
    private readonly List<ChatMessage> _messages = [];

    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Die letzte Nachricht des Nutzers, falls es eine gibt.</summary>
    public ChatMessage? LastUserMessage => _messages.LastOrDefault(m => m.Role == ChatRole.User);

    public void Add(ChatRole role, string content) =>
        _messages.Add(new ChatMessage(role, content, DateTime.Now));

    public void AddUser(string content) => Add(ChatRole.User, content);

    public void AddAssistant(string content) => Add(ChatRole.Assistant, content);

    public void Clear() => _messages.Clear();
}
