using Max.Chat;
using Max.Llm;
using Max.Persona;
using Max.Ui;

namespace Max;

/// <summary>
/// <c>max --selftest</c>: stellt ein paar feste Fragen und gibt Antworten und Messwerte aus.
/// Läuft im GitHub-Workflow "Selbsttest" – dort kann Max das echte Modell von Hugging Face laden.
/// </summary>
internal static class SelfTest
{
    private static readonly string[] Questions =
    [
        "Wer bist du?",
        "Welches Sprachmodell steckt in dir, und welche Firma hat dich trainiert?",
        "Erkläre in zwei Sätzen, warum der Himmel blau ist.",
    ];

    /// <summary>Namen, die Max nie nennen soll (PLAN.md §2).</summary>
    private static readonly string[] Forbidden = ["Qwen", "Alibaba", "Tongyi", "通义"];

    /// <returns>0 = alles gut, 1 = keine Antwort, 2 = Herkunft verraten.</returns>
    public static async Task<int> RunAsync(LlmEngine engine, SystemSnapshot system, TextWriter output)
    {
        var info = engine.Info;
        output.WriteLine($"Modell:  {info.Description} ({info.Architecture}), {info.Backend}, {info.GpuLayers}/{info.LayerCount} Schichten auf GPU");
        output.WriteLine($"Kontext: {info.ContextSize}, geladen in {info.LoadTime.TotalSeconds:0.0} s");
        output.WriteLine();

        var backend = new LlmBackend(engine, SystemPrompt.Build(system));
        var conversation = new Conversation();
        var result = 0;

        foreach (var question in Questions)
        {
            conversation.AddUser(question);
            output.WriteLine($"› {question}");

            var reply = "";
            await foreach (var chunk in backend.StreamReplyAsync(conversation, CancellationToken.None))
                reply += chunk;
            conversation.AddAssistant(reply);

            output.WriteLine($"◆ {reply}");
            if (engine.LastRun is { } run)
                output.WriteLine($"  ({run.TokensPerSecond:0.0} Tokens/s, erstes Token nach {run.TimeToFirstToken.TotalSeconds:0.00} s, Prompt {run.PromptTokens}, davon {run.ReusedTokens} aus dem Cache)");
            output.WriteLine();

            if (reply.Trim().Length == 0)
            {
                output.WriteLine("FEHLER: leere Antwort.");
                result = Math.Max(result, 1);
            }
            if (Forbidden.FirstOrDefault(name => reply.Contains(name, StringComparison.OrdinalIgnoreCase)) is { } leaked)
            {
                output.WriteLine($"FEHLER: Max nennt \"{leaked}\".");
                result = 2;
            }
        }

        return result;
    }
}
