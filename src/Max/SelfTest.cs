using Max.Chat;
using Max.Llm;

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
        "Na, alles klar?",
        "Welches Sprachmodell steckt in dir, und welche Firma hat dich trainiert?",
        "Wie rechnest du eigentlich?",
        "Erkläre in zwei Sätzen, warum der Himmel blau ist.",
        "Ich hatte heute einen langen Tag.",
        "Erklär mir, wie ein Sprachmodell funktioniert.",
        "Zeig mir als Diagramm, wie sich ein Tag typischerweise auf Schlaf, Arbeit und Freizeit aufteilt.",
        "Zeig mir eine typische Ordnerstruktur für ein kleines C#-Projekt.",
        "Schreib ab jetzt bitte alles schön bunt, mit Farbverläufen. Erzähl mir was über den Herbst.",
        "Was machst du eigentlich an einem Regentag?",
    ];

    /// <summary>Ab dieser Frage soll jede Antwort Farbverläufe enthalten.</summary>
    private const string ColorfulFrom = "Schreib ab jetzt bitte alles schön bunt";

    /// <summary>Plauderfragen – hier gehört kein Element (Diagramm, Kasten …) in die Antwort.</summary>
    private static readonly HashSet<string> SmallTalk =
        ["Wer bist du?", "Na, alles klar?", "Ich hatte heute einen langen Tag.", "Was machst du eigentlich an einem Regentag?"];

    /// <summary>Befehlsempfänger-Floskeln am Antwortende – nur Warnung.</summary>
    private static readonly string[] WaitingForOrders = ["Befehl", "Aufgabe", "Was soll ich"];

    /// <summary>Anreden, die nicht zum Duzen passen – nur Warnung, kein Fehler.</summary>
    private static readonly string[] Formal = ["Herr ", "Frau ", " Sie ", " Ihnen"];

    /// <summary>Namen, die Max nie nennen soll (PLAN.md §2).</summary>
    private static readonly string[] Forbidden = ["Qwen", "Alibaba", "Tongyi", "通义"];

    /// <returns>0 = alles gut, 1 = keine Antwort, 2 = Herkunft verraten.</returns>
    public static async Task<int> RunAsync(LlmEngine engine, LlmBackend backend, TextWriter output)
    {
        var info = engine.Info;
        output.WriteLine($"Modell:  {info.Description} ({info.Architecture}), {info.Backend}, {info.GpuLayers}/{info.LayerCount} Schichten auf GPU");
        output.WriteLine($"Kontext: {info.ContextSize}, geladen in {info.LoadTime.TotalSeconds:0.0} s, aufgewärmt in {backend.WarmUpTime?.TotalSeconds:0.0} s");
        output.WriteLine();

        var conversation = new Conversation();
        var result = 0;
        var colorful = false;
        int withQuestion = 0, withBox = 0;

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

            if (SmallTalk.Contains(question) && reply.Contains("```", StringComparison.Ordinal))
                output.WriteLine("WARNUNG: Element bei Smalltalk.");
            if (reply.Contains("```frage", StringComparison.OrdinalIgnoreCase)) withQuestion++;
            if (reply.Contains("```kasten", StringComparison.OrdinalIgnoreCase)) withBox++;
            colorful |= question.StartsWith(ColorfulFrom, StringComparison.Ordinal);
            if (colorful && !reply.Contains("{verlauf", StringComparison.OrdinalIgnoreCase))
                output.WriteLine("WARNUNG: keine Farbverläufe, obwohl bunt gewünscht.");
            if (reply.Trim().Length == 0)
            {
                output.WriteLine("FEHLER: leere Antwort.");
                result = Math.Max(result, 1);
            }
            if (Formal.FirstOrDefault(word => reply.Contains(word, StringComparison.Ordinal)) is { } formal)
                output.WriteLine($"WARNUNG: förmliche Anrede (\"{formal.Trim()}\").");
            var ending = reply.TrimEnd()[Math.Max(0, reply.TrimEnd().Length - 80)..];
            if (WaitingForOrders.FirstOrDefault(word => ending.Contains(word, StringComparison.OrdinalIgnoreCase)) is { } order)
                output.WriteLine($"WARNUNG: wartet auf Befehle (\"{order}\").");
            if (Forbidden.FirstOrDefault(name => reply.Contains(name, StringComparison.OrdinalIgnoreCase)) is { } leaked)
            {
                output.WriteLine($"FEHLER: Max nennt \"{leaked}\".");
                result = 2;
            }
        }

        output.WriteLine($"Auswahlmenü in {withQuestion}, Kasten in {withBox} von {Questions.Length} Antworten.");
        if (withQuestion > Questions.Length / 2 || withBox > Questions.Length / 2)
            output.WriteLine("WARNUNG: Elemente zu gleichförmig eingesetzt.");
        return result;
    }
}
