using Max.Chat;

namespace Max.Llm;

/// <summary>Wie ein Gesprächsverlauf für das Modell aussieht. Jedes Modell erwartet ein eigenes Format.</summary>
internal interface IChatTemplate
{
    /// <summary>Eine vollständige Nachricht, z. B. <c>&lt;|im_start|&gt;user\nHallo&lt;|im_end|&gt;\n</c>.</summary>
    string Message(ChatRole role, string content);

    /// <summary>Beginn einer Antwort von Max; danach schreibt das Modell.</summary>
    string AssistantStart { get; }

    /// <summary>Was nach einer Antwort von Max steht (das Modell selbst hört davor auf).</summary>
    string AssistantEnd { get; }
}

/// <summary>
/// ChatML, wie es die Qwen-Familie nutzt.
/// Jede Antwort beginnt mit einem leeren Denk-Block: So antwortet das Modell direkt, ohne erst
/// laut nachzudenken. Auch Antworten im Verlauf behalten ihn – dann passt der Verlauf Token für
/// Token zu dem, was das Modell tatsächlich gesehen hat, und der Cache lässt sich weiterverwenden.
/// </summary>
internal sealed class ChatMlTemplate : IChatTemplate
{
    public string AssistantStart => "<|im_start|>assistant\n<think>\n\n</think>\n\n";
    public string AssistantEnd => "<|im_end|>\n";

    public string Message(ChatRole role, string content) => role == ChatRole.Assistant
        ? AssistantStart + content + AssistantEnd
        : $"<|im_start|>{RoleName(role)}\n{content}<|im_end|>\n";

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}
