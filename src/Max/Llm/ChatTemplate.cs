using Max.Chat;

namespace Max.Llm;

/// <summary>Wie ein Gesprächsverlauf für das Modell aussieht. Jedes Modell erwartet ein eigenes Format.</summary>
internal interface IChatTemplate
{
    /// <summary>Eine vollständige Nachricht, z. B. <c>&lt;|im_start|&gt;user\nHallo&lt;|im_end|&gt;\n</c>.</summary>
    string Message(ChatRole role, string content);

    /// <summary>Beginn einer Antwort von Max ohne Nachdenken; danach schreibt das Modell. So stehen Antworten auch im Verlauf.</summary>
    string AssistantStart { get; }

    /// <summary>Beginn einer Antwort mit Nachdenken: Das Modell schreibt zuerst seine Gedanken, dann den Abschluss des Denk-Blocks.</summary>
    string AssistantStartThinking { get; }

    /// <summary>Beendet das Nachdenken, wenn das Budget verbraucht ist.</summary>
    string ForcedThinkingEnd { get; }

    /// <summary>Was nach einer Antwort von Max steht (das Modell selbst hört davor auf).</summary>
    string AssistantEnd { get; }
}

/// <summary>
/// ChatML, wie es die Qwen-Familie nutzt.
/// Ohne Nachdenken beginnt eine Antwort mit einem leeren Denk-Block – so antwortet das Modell direkt.
/// Antworten im Verlauf stehen immer so da: Das Nachdenken gehört nicht in den Verlauf (spart Kontext),
/// siehe <see cref="LlmBackend"/>.
/// </summary>
internal sealed class ChatMlTemplate : IChatTemplate
{
    public string AssistantStart => "<|im_start|>assistant\n<think>\n\n</think>\n\n";
    public string AssistantStartThinking => "<|im_start|>assistant\n<think>\n";
    public string ForcedThinkingEnd => "\n\nGenug nachgedacht.\n</think>\n\n";
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
