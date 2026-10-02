using Max.Chat;

namespace Max.Llm;

/// <summary>
/// Entscheidet, welche Nachrichten noch in den Kontext passen. Der System-Prompt und die neueste
/// Nachricht bleiben immer; von vorn fallen ganze Runden (Frage + Antwort) weg.
/// Gekürzt wird mit Luft (auf <see cref="RefillRatio"/> des Budgets), damit nicht jede neue Runde
/// den Anfang verschiebt – sonst müsste das Modell bei jeder Antwort alles neu lesen.
/// </summary>
internal sealed class ContextWindow(int budget)
{
    internal const double RefillRatio = 0.7;

    private ChatMessage? _firstKept;

    public int Budget { get; } = budget;

    /// <summary>
    /// Liefert den Index der ersten Nachricht, die noch mitgeschickt wird.
    /// </summary>
    /// <param name="messages">Der Verlauf ohne System-Prompt.</param>
    /// <param name="fixedCost">Tokens, die immer anfallen (System-Prompt, Antwort-Beginn).</param>
    /// <param name="cost">Tokens einer Nachricht.</param>
    /// <param name="minimum">Davor nie – diese Nachrichten hat Max sich schon zusammengefasst.</param>
    public int FirstIncluded(IReadOnlyList<ChatMessage> messages, int fixedCost, Func<ChatMessage, int> cost, int minimum = 0)
    {
        var start = Compute(messages, fixedCost, cost, minimum);
        if (messages.Count > 0)
            _firstKept = messages[start];
        return start;
    }

    /// <summary>Wo der Verlauf beginnen würde – ohne sich das zu merken (zum Vorausschauen).</summary>
    public int Compute(IReadOnlyList<ChatMessage> messages, int fixedCost, Func<ChatMessage, int> cost, int minimum = 0)
    {
        if (messages.Count == 0)
            return 0;

        // Bisheriger Anfang, falls es ihn noch gibt (nach /clear nicht mehr).
        var start = _firstKept is null ? 0 : IndexOf(messages, _firstKept);
        if (start < 0)
            start = 0;
        start = Math.Max(start, Math.Min(minimum, messages.Count - 1));

        if (Total(messages, start, fixedCost, cost) > Budget)
        {
            var target = (int)(Budget * RefillRatio);
            while (start < messages.Count - 1 && Total(messages, start, fixedCost, cost) > target)
            {
                start++;
                // Nicht mit einer Antwort beginnen – immer mit einer Frage des Nutzers.
                while (start < messages.Count - 1 && messages[start].Role != ChatRole.User)
                    start++;
            }
        }
        return start;
    }

    /// <summary>Der zuletzt gemerkte Anfang (für die Notiz vom Anfang, siehe LlmBackend) – oder 0.</summary>
    public int Current(IReadOnlyList<ChatMessage> messages) => _firstKept is null ? 0 : Math.Max(0, IndexOf(messages, _firstKept));

    private static int Total(IReadOnlyList<ChatMessage> messages, int start, int fixedCost, Func<ChatMessage, int> cost)
    {
        var total = fixedCost;
        for (var i = start; i < messages.Count; i++)
            total += cost(messages[i]);
        return total;
    }

    private static int IndexOf(IReadOnlyList<ChatMessage> messages, ChatMessage message)
    {
        for (var i = 0; i < messages.Count; i++)
            if (ReferenceEquals(messages[i], message))
                return i;
        return -1;
    }
}
