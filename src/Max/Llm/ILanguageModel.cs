namespace Max.Llm;

/// <summary>Ein erzeugtes Stück: das Token und der Text, der dadurch fertig geworden ist (kann leer sein).</summary>
internal readonly record struct GeneratedPiece(int Token, string Text);

internal sealed record SamplingSettings(
    float Temperature = 0.7f,
    float TopP = 0.8f,
    int TopK = 20,
    float MinP = 0f,
    float RepeatPenalty = 1.05f,
    int MaxTokens = 2048);

/// <summary>
/// Das Sprachmodell aus Sicht von <see cref="LlmBackend"/>: Tokens rein, Tokens raus.
/// Die echte Umsetzung ist <see cref="LlmEngine"/>; Tests nehmen eine Attrappe.
/// </summary>
internal interface ILanguageModel
{
    int ContextSize { get; }

    /// <summary>Text → Tokens. Steuer-Tokens wie <c>&lt;|im_start|&gt;</c> werden erkannt.</summary>
    IReadOnlyList<int> Tokenize(string text);

    /// <summary>
    /// Verarbeitet den Prompt und erzeugt die Antwort Stück für Stück, bis das Modell fertig ist,
    /// <paramref name="ct"/> abbricht oder der Kontext voll ist.
    /// </summary>
    IAsyncEnumerable<GeneratedPiece> GenerateAsync(IReadOnlyList<int> prompt, SamplingSettings sampling, CancellationToken ct);

    /// <summary>
    /// Verarbeitet einen Prompt-Anfang nur in den Cache, ohne etwas zu erzeugen ("Aufwärmen").
    /// Beginnt der nächste Prompt genauso, geht die Antwort sofort los.
    /// </summary>
    Task PrefillAsync(IReadOnlyList<int> prompt, CancellationToken ct);
}
