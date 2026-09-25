namespace Max.Llm;

internal sealed record SamplingSettings(
    float Temperature = 0.7f,
    float TopP = 0.8f,
    int TopK = 20,
    float MinP = 0f,
    float RepeatPenalty = 1.05f,
    int MaxTokens = 2048)
{
    /// <summary>Empfehlung für das Nachdenken (Qwen): etwas kühler, breitere Auswahl.</summary>
    public static SamplingSettings Thinking { get; } = new(Temperature: 0.6f, TopP: 0.95f, TopK: 20, RepeatPenalty: 1.0f);
}

/// <summary>
/// Das Sprachmodell aus Sicht von <see cref="LlmBackend"/> – Bausteine statt fertiger Schleife:
/// Tokens in den Cache rechnen, ein Token ziehen, Zwischenstände merken und wiederherstellen.
/// Die echte Umsetzung ist <see cref="LlmEngine"/>; Tests nehmen eine Attrappe.
/// Nicht für gleichzeitige Aufrufe gedacht – das Backend ruft nacheinander auf.
/// </summary>
internal interface ILanguageModel
{
    int ContextSize { get; }

    /// <summary>Wie viele Tokens gerade im Cache stehen.</summary>
    int CachedCount { get; }

    /// <summary>Text → Tokens. Steuer-Tokens wie <c>&lt;|im_start|&gt;</c> werden erkannt.</summary>
    IReadOnlyList<int> Tokenize(string text);

    /// <summary>
    /// Bringt den Cache auf den Stand von <paramref name="prompt"/>. Beginnt er gleich wie der Cache,
    /// wird nur der neue Teil gerechnet. Danach kann das nächste Token gezogen werden.
    /// </summary>
    /// <returns>Wie viele Tokens aus dem Cache wiederverwendet wurden.</returns>
    Task<int> PrefillAsync(IReadOnlyList<int> prompt, CancellationToken ct);

    /// <summary>Hängt Tokens an den Cache an (z. B. das gerade gezogene).</summary>
    Task AppendAsync(IReadOnlyList<int> tokens, CancellationToken ct);

    /// <summary>Ein Sampler für die nächsten Tokens, optional mit Grammatik (GBNF), die nur gültige Tokens zulässt.</summary>
    ITokenSampler CreateSampler(SamplingSettings settings, string? grammar = null, uint? seed = null);

    /// <summary>Setzt Tokens wieder zu Text zusammen – auch Zeichen, die über mehrere Tokens verteilt sind.</summary>
    ITokenDecoder CreateDecoder();

    bool IsEndOfGeneration(int token);

    /// <summary>Merkt sich den aktuellen Stand. Null, wenn das nicht geht.</summary>
    ModelCheckpoint? Checkpoint();

    /// <summary>
    /// Kehrt zu einem gemerkten Stand zurück. Die Logits sind danach nicht gültig –
    /// vor dem nächsten Ziehen muss mindestens ein Token angehängt werden. Klappt das Zurückkehren nicht, wird der Cache geleert
    /// (der nächste <see cref="PrefillAsync"/> rechnet dann alles neu) und false geliefert.
    /// </summary>
    bool Restore(ModelCheckpoint checkpoint);
}

/// <summary>Zieht das nächste Token aus den aktuellen Logits des Modells.</summary>
internal interface ITokenSampler : IDisposable
{
    int Sample();

    /// <summary>Für Tokens, die nicht gezogen, sondern eingefügt wurden: Grammatik und Wiederholungsstrafe nachführen.</summary>
    void Accept(int token);

    /// <summary>Ob die Grammatik tatsächlich aktiv ist (sie kann beim Laden scheitern).</summary>
    bool HasGrammar { get; }
}

internal interface ITokenDecoder
{
    /// <summary>Nimmt ein Token an und liefert den Text, der dadurch fertig geworden ist (kann leer sein).</summary>
    string Add(int token);
}

/// <summary>Ein gemerkter Stand des Modells.</summary>
internal abstract class ModelCheckpoint : IDisposable
{
    /// <summary>Wie viele Tokens zu diesem Zeitpunkt im Cache standen.</summary>
    public abstract int TokenCount { get; }

    public virtual void Dispose() { }
}
