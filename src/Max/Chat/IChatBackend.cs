namespace Max.Chat;

/// <summary>
/// Erzeugt Max' Antwort auf den bisherigen Verlauf – Stück für Stück, damit sie
/// schon während des Entstehens angezeigt werden kann. Vor der Antwort kann Nachdenken kommen.
/// </summary>
internal interface IChatBackend
{
    IAsyncEnumerable<ReplyChunk> StreamReplyAsync(Conversation conversation, CancellationToken ct);
}

/// <summary>Ein Stück der Antwort – oder, mit <paramref name="IsThinking"/>, des Nachdenkens davor.</summary>
internal readonly record struct ReplyChunk(string Text, bool IsThinking = false);
