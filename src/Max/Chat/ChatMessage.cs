namespace Max.Chat;

/// <summary>Wer eine Nachricht geschrieben hat.</summary>
internal enum ChatRole
{
    /// <summary>Anweisungen an das Modell (System-Prompt).</summary>
    System,

    /// <summary>Der Mensch vor dem Bildschirm.</summary>
    User,

    /// <summary>Max selbst.</summary>
    Assistant,

    /// <summary>Ergebnis eines Tool-Aufrufs (Phase 2).</summary>
    Tool,
}

/// <summary>Eine einzelne Nachricht im Gesprächsverlauf.</summary>
internal sealed record ChatMessage(ChatRole Role, string Content, DateTime Timestamp);
