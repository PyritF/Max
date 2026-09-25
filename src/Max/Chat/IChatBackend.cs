namespace Max.Chat;

/// <summary>
/// Erzeugt Max' Antwort auf den bisherigen Verlauf – Stück für Stück, damit sie
/// schon während des Entstehens angezeigt werden kann.
/// Jetzt: <see cref="PlaceholderBackend"/>. Später: das lokale Sprachmodell.
/// </summary>
internal interface IChatBackend
{
    IAsyncEnumerable<string> StreamReplyAsync(Conversation conversation, CancellationToken ct);
}
