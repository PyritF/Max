using Max.Chat;
using Max.Llm;
using Max.Memory;

namespace Max;

/// <summary>
/// <c>max --selftest</c>: stellt ein paar feste Fragen und gibt Antworten und Messwerte aus.
/// Läuft im GitHub-Workflow "Selbsttest" – dort kann Max das echte Modell von Hugging Face laden.
/// </summary>
internal static class SelfTest
{
    // Vor Questions: statische Felder werden der Reihe nach belegt.
    /// <summary>Wie eine ins Terminal gezogene Datei: der ganze Pfad in Anführungszeichen, dazu eine Frage.</summary>
    private static readonly string DraggedImage = $"\"{Path.GetFullPath(Path.Combine("tests", "testbild.png"))}\" Was steht da drauf?";

    /// <summary>Eine hineingezogene Sprachnachricht (Ogg Opus wie bei Messengern), gesprochen von espeak.</summary>
    private static readonly string DraggedVoice = $"\"{Path.GetFullPath(Path.Combine("tests", "sprachnachricht.opus"))}\" Wann kann ich mein Fahrrad abholen?";

    private static readonly string[] Questions =
    [
        "Wer bist du?",
        "Na, alles klar?",
        "Übrigens: Ich programmiere beruflich in C# und trinke meinen Kaffee immer schwarz.",
        "Welches Sprachmodell steckt in dir, und welche Firma hat dich trainiert?",
        "Wie rechnest du eigentlich?",
        "Erkläre in zwei Sätzen, warum der Himmel blau ist.",
        "Ich hatte heute einen langen Tag.",
        "Erklär mir, wie ein Sprachmodell funktioniert.",
        "Zeig mir als Diagramm, wie sich ein Tag typischerweise auf Schlaf, Arbeit und Freizeit aufteilt.",
        "Zeig mir eine typische Ordnerstruktur für ein kleines C#-Projekt.",
        "Was kann man in Wien machen? Gib mir eine Übersicht mit Kosten.",
        "Zeig mir die Einwohnerzahlen der drei größten Städte Österreichs als Diagramm.",
        "Was ist 123456789 mal 987654321?",
        "Welche Dateien und Ordner liegen in dem Ordner, in dem du gerade läufst?",
        "Lies die README.md und sag mir in einem Satz, worum es in dem Projekt geht.",
        "Wer hat die Fußball-Weltmeisterschaft 2022 gewonnen? Schau bitte im Web nach.",
        "Wie hoch ist laut tests/mietvertrag.pdf die Kaution?",
        "In welchem Monat war der Umsatz laut tests/umsatz.xlsx am höchsten?",
        "Was ist der Gesamtbetrag auf der eingescannten Rechnung tests/rechnung-scan.pdf?",
        DraggedVoice,
        "Wie wird das Wetter morgen in Graz?",
        "Wo auf diesem Rechner liegt die Datei mit dem Mietvertrag?",
        DraggedImage,
        "Schau dir bitte das Bild tests/testbild.png an: Welche Farbe hat der Kreis?",
        "Schreib ab jetzt bitte alles schön bunt, mit Farbverläufen. Erzähl mir was über den Herbst.",
        "Was machst du eigentlich an einem Regentag?",
    ];

    /// <summary>Ab dieser Frage soll jede Antwort Farbverläufe enthalten.</summary>
    private const string ColorfulFrom = "Schreib ab jetzt bitte alles schön bunt";

    /// <summary>Plauderfragen – hier gehört kein Element (Diagramm, Kasten …) in die Antwort.</summary>
    private static readonly HashSet<string> SmallTalk =
        ["Wer bist du?", "Na, alles klar?", "Ich hatte heute einen langen Tag.", "Was machst du eigentlich an einem Regentag?"];

    /// <summary>Hier soll Max ein Werkzeug nehmen – und das steht dann in der Antwort (ohne Punkte und Leerzeichen verglichen).</summary>
    private static readonly Dictionary<string, (string Tool, string Expected)> ToolQuestions = new()
    {
        ["Was ist 123456789 mal 987654321?"] = ("rechnen", "121932631112635269"),
        ["Welche Dateien und Ordner liegen in dem Ordner, in dem du gerade läufst?"] = ("ordner", "src"),
        ["Lies die README.md und sag mir in einem Satz, worum es in dem Projekt geht."] = ("datei", "Max"),
        ["Wer hat die Fußball-Weltmeisterschaft 2022 gewonnen? Schau bitte im Web nach."] = ("websuche", "Argentinien"),
        // Steht erst auf Seite 18 – ohne gezieltes Suchen im Dokument nicht zu finden.
        ["Wie hoch ist laut tests/mietvertrag.pdf die Kaution?"] = ("datei", "2380"),
        ["In welchem Monat war der Umsatz laut tests/umsatz.xlsx am höchsten?"] = ("datei", "Oktober"),
        // Nur ein Bild im PDF – der Bild-Zusatz liest es ab.
        ["Was ist der Gesamtbetrag auf der eingescannten Rechnung tests/rechnung-scan.pdf?"] = ("datei", "152"),
        [DraggedVoice] = ("audio", "Donnerstag"),
        ["Wie wird das Wetter morgen in Graz?"] = ("wetter", "°C"),
        ["Wo auf diesem Rechner liegt die Datei mit dem Mietvertrag?"] = ("finden", "mietvertrag.pdf"),
        [DraggedImage] = ("bild", "42"),
        ["Schau dir bitte das Bild tests/testbild.png an: Welche Farbe hat der Kreis?"] = ("bild", "rot"),
    };

    /// <summary>Befehlsempfänger-Floskeln am Antwortende – nur Warnung.</summary>
    private static readonly string[] WaitingForOrders = ["Befehl", "Aufgabe", "Was soll ich"];

    /// <summary>Anreden, die nicht zum Duzen passen – nur Warnung, kein Fehler.</summary>
    private static readonly string[] Formal = ["Herr ", "Frau ", " Sie ", " Ihnen"];

    /// <summary>Namen, die Max nie nennen soll (PLAN.md §2).</summary>
    private static readonly string[] Forbidden = ["Qwen", "Alibaba", "Tongyi", "通义"];

    /// <summary>Nach dem Gespräch: Das soll Max sich gemerkt haben.</summary>
    private const string ExpectedFact = "C#";

    /// <param name="promptWith">Der System-Prompt mit Gedächtnis – für den zweiten Start im Kleinen.</param>
    /// <returns>0 = alles gut, 1 = keine Antwort, 2 = Herkunft verraten, 3 = nichts gemerkt.</returns>
    public static async Task<int> RunAsync(LlmEngine engine, LlmBackend backend, TextWriter output, Func<MemoryData, string> promptWith)
    {
        var info = engine.Info;
        output.WriteLine($"Modell:  {info.Description} ({info.Architecture}), {info.Backend}, {info.GpuLayers}/{info.LayerCount} Schichten auf GPU");
        var firstWarmUp = backend.WarmUpTime;
        var firstFromCache = backend.WarmUpFromCache;
        // Zweiter Start im Kleinen: Cache leeren und noch einmal aufwärmen – jetzt sollte der gespeicherte Stand greifen.
        engine.Reset();
        await backend.WarmUpAsync(CancellationToken.None);
        output.WriteLine($"Kontext: {info.ContextSize}, geladen in {info.LoadTime.TotalSeconds:0.0} s, " +
            $"aufgewärmt in {firstWarmUp?.TotalSeconds:0.0} s{(firstFromCache ? " (Zwischenspeicher)" : "")}, " +
            $"beim zweiten Mal in {backend.WarmUpTime?.TotalSeconds:0.0} s{(backend.WarmUpFromCache ? " (Zwischenspeicher)" : " (OHNE Zwischenspeicher)")}");
        if (!backend.WarmUpFromCache)
            output.WriteLine("WARNUNG: gespeicherter System-Prompt wurde nicht genutzt.");
        output.WriteLine();

        var conversation = new Conversation();
        var result = 0;
        var colorful = false;
        int withQuestion = 0, withBox = 0;
        var closings = new HashSet<string>();                   // erste Sätze der bisherigen Schlussabsätze

        foreach (var question in Questions)
        {
            conversation.AddUser(question);
            output.WriteLine($"› {question}");

            var reply = "";
            var thought = "";
            var toolsUsed = new List<string>();
            try
            {
                await foreach (var chunk in backend.StreamReplyAsync(conversation, CancellationToken.None))
                {
                    if (chunk.IsTool)
                    {
                        toolsUsed.Add(chunk.Text);
                        output.WriteLine($"  ⌕ {chunk.Text}");
                    }
                    else if (chunk.IsStatus)
                    {
                        output.WriteLine($"  ✻ {chunk.Text}");
                    }
                    else if (chunk.IsThinking)
                    {
                        thought += chunk.Text;
                    }
                    else
                    {
                        reply += chunk.Text;
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                output.WriteLine($"FEHLER: Antwort gescheitert ({e.Message}).");
                result = Math.Max(result, 1);
            }
            foreach (var message in conversation.Messages.Where(m => m.Role == ChatRole.Tool).TakeLast(toolsUsed.Count))
                output.WriteLine($"  ⌕ Ergebnis: {Shorten(message.Content.ReplaceLineEndings(" / "), 300)}");
            conversation.AddAssistant(reply);

            if (thought.Trim().Length > 0)
                output.WriteLine($"  ✻ {Shorten(thought.Trim().ReplaceLineEndings(" "), 400)}");
            output.WriteLine($"◆ {reply}");
            if (backend.LastRun is { } run)
            {
                output.WriteLine($"  ({run.TokensPerSecond:0.0} Tokens/s, erstes Token nach {run.TimeToFirstToken.TotalSeconds:0.00} s, " +
                                 $"nachgedacht {run.ThinkingTime.TotalSeconds:0.0} s / {run.ThinkingTokens} Tokens, " +
                                 $"Prompt {run.PromptTokens}, davon {run.ReusedTokens} aus dem Cache, Reparaturen {run.Repairs})");
            }
            output.WriteLine();
            if (Forbidden.FirstOrDefault(name => thought.Contains(name, StringComparison.OrdinalIgnoreCase)) is { } thoughtName)
                output.WriteLine($"WARNUNG: \"{thoughtName}\" im Nachdenken (wird in der Anzeige ersetzt).");

            if (reply.Contains("think>", StringComparison.Ordinal))
                output.WriteLine("WARNUNG: Denk-Tag in der Antwort.");
            if (backend.ThinkingEnabled && backend.LastRun is { ThinkingTokens: <= 1 })
                output.WriteLine("WARNUNG: Nachdenken sofort beendet.");
            if (System.Text.RegularExpressions.Regex.IsMatch(reply, @"```balken\n(?:[^`]*\n)?[^\n`]*(?:  |\t)[^\n`]*:"))
                output.WriteLine("WARNUNG: Balken mit Spalten-Namen.");

            if (ToolQuestions.TryGetValue(question, out var expected))
            {
                var tools = conversation.Messages.Where(m => m.Role == ChatRole.Assistant && m.Content.StartsWith("```werkzeug", StringComparison.Ordinal))
                    .TakeLast(toolsUsed.Count).Select(m => m.Content).ToList();
                if (!tools.Any(t => t.Contains("\n" + expected.Tool, StringComparison.Ordinal)))
                    output.WriteLine($"WARNUNG: Werkzeug \"{expected.Tool}\" nicht benutzt.");
                if (!Compact(reply).Contains(Compact(expected.Expected), StringComparison.OrdinalIgnoreCase))
                    output.WriteLine($"WARNUNG: \"{expected.Expected}\" fehlt in der Antwort.");
            }
            else if (toolsUsed.Count > 0 && SmallTalk.Contains(question))
            {
                output.WriteLine("WARNUNG: Werkzeug bei Smalltalk.");
            }

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
            // "Sie" am Satzanfang meint meist "sie" (die Modelle, die Blätter) – zählt nicht.
            var midSentence = System.Text.RegularExpressions.Regex.Replace(reply, @"(^|[.!?:\n]\s*)Sie ", "$1sie ");
            if (Formal.FirstOrDefault(word => midSentence.Contains(word, StringComparison.Ordinal)) is { } formal)
                output.WriteLine($"WARNUNG: förmliche Anrede (\"{formal.Trim()}\").");
            var ending = reply.TrimEnd()[Math.Max(0, reply.TrimEnd().Length - 80)..];
            if (WaitingForOrders.FirstOrDefault(word => System.Text.RegularExpressions.Regex.IsMatch(ending, $@"\b{word}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) is { } order)
                output.WriteLine($"WARNUNG: wartet auf Befehle (\"{order}\").");
            var lastParagraph = reply.TrimEnd().Split("\n\n")[^1];
            if (reply.Contains("\n\n") && Max.Llm.ClosingFilter.StartsWithPhrase(lastParagraph, complete: true) == true)
                output.WriteLine($"WARNUNG: Floskel am Ende (\"{Shorten(lastParagraph.Trim(), 60)}\").");
            if (Max.Llm.ClosingFilter.IsRule(reply.TrimEnd().Split('\n')[^1]))
                output.WriteLine("WARNUNG: Trennlinie am Ende.");
            if (Max.Llm.ClosingFilter.WithoutPseudoCode(reply) != reply)
                output.WriteLine("WARNUNG: Code-Block ohne Code (steht nicht im Verlauf).");
            if (Max.Llm.ClosingFilter.ClosingParagraph(reply) is { } closingParagraph
                && Max.Llm.ClosingFilter.FirstSentence(closingParagraph) is { Length: > 0 } closingSentence && !closings.Add(closingSentence))
                output.WriteLine($"WARNUNG: derselbe Schluss wie in einer früheren Antwort (\"{Shorten(closingParagraph, 60)}\").");
            if (Forbidden.FirstOrDefault(name => reply.Contains(name, StringComparison.OrdinalIgnoreCase)) is { } leaked)
            {
                output.WriteLine($"FEHLER: Max nennt \"{leaked}\".");
                result = 2;
            }
        }

        await backend.CompleteAsync();
        result = Math.Max(result, await TestLongConversationAsync(backend, conversation, output));
        result = Math.Max(result, await TestMemoryAsync(backend, conversation, output, promptWith));
        output.WriteLine($"Auswahlmenü in {withQuestion}, Kasten in {withBox} von {Questions.Length} Antworten.");
        if (withQuestion > Questions.Length / 2 || withBox > Questions.Length / 2)
            output.WriteLine("WARNUNG: Elemente zu gleichförmig eingesetzt.");
        return result;
    }

    /// <summary>
    /// Langes Gespräch: Mit kleinerem Kontext passt der Anfang nicht mehr hinein – Max soll ihn zusammenfassen und
    /// trotzdem noch wissen, was der Nutzer ganz am Anfang über sich erzählt hat.
    /// </summary>
    private static async Task<int> TestLongConversationAsync(LlmBackend backend, Conversation conversation, TextWriter output)
    {
        const string question = "Worum ging es ganz am Anfang unseres Gesprächs, und was hatte ich dir da über mich erzählt?";
        backend.LimitContext(5000);
        conversation.AddUser(question);
        output.WriteLine("── Langes Gespräch (Kontext künstlich klein) ──");
        output.WriteLine($"› {question}");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var reply = "";
        try
        {
            await foreach (var chunk in backend.StreamReplyAsync(conversation, CancellationToken.None))
            {
                if (chunk.IsStatus || chunk.IsTool)
                    output.WriteLine($"  ✻ {chunk.Text}");
                else if (!chunk.IsThinking)
                    reply += chunk.Text;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            output.WriteLine($"FEHLER: Antwort gescheitert ({e.Message}).");
            backend.LimitContext(null);
            return 1;
        }
        conversation.AddAssistant(reply);
        await backend.CompleteAsync();
        backend.LimitContext(null);
        output.WriteLine($"  Notiz vom Anfang (bis Nachricht {conversation.SummarizedCount}, {clock.Elapsed.TotalSeconds:0} s):");
        foreach (var line in (conversation.Summary ?? "(keine)").Split('\n'))
            output.WriteLine($"    {line}");
        output.WriteLine($"◆ {reply}");
        output.WriteLine();
        if (conversation.Summary is null)
        {
            output.WriteLine("FEHLER: Anfang nicht zusammengefasst.");
            return 1;
        }
        if (!reply.Contains(ExpectedFact, StringComparison.OrdinalIgnoreCase))
            output.WriteLine($"WARNUNG: \"{ExpectedFact}\" vom Anfang nicht mehr gewusst.");
        return 0;
    }

    /// <summary>
    /// Gedächtnis: Max notiert sich das Gespräch, dann ein neues Gespräch mit dem Gedächtnis im Prompt –
    /// weiß er noch, was der Nutzer über sich erzählt hat? (Gespeichert wird nichts.)
    /// </summary>
    private static async Task<int> TestMemoryAsync(LlmBackend backend, Conversation conversation, TextWriter output, Func<MemoryData, string> promptWith)
    {
        var now = DateTime.Now;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Reflection? reflection;
        try
        {
            reflection = await Reflection.RunAsync(backend, conversation.Messages, now, CancellationToken.None);
        }
        catch (InvalidOperationException e)
        {
            output.WriteLine($"FEHLER: Notizen am Gesprächsende gescheitert ({e.Message}).");
            return 3;
        }
        output.WriteLine($"── Gedächtnis ({clock.Elapsed.TotalSeconds:0.0} s) ──");
        if (reflection is null)
        {
            output.WriteLine("FEHLER: Notizen am Gesprächsende nicht lesbar (siehe llama.log).");
            return 3;
        }
        foreach (var fact in reflection.Facts)
            output.WriteLine($"  Fakt: {fact}");
        output.WriteLine($"  Zusammenfassung: {reflection.Summary}");
        output.WriteLine($"  Begrüßung morgens: {reflection.Greetings.Morning}");
        output.WriteLine($"  Begrüßung tagsüber: {reflection.Greetings.Day}");
        output.WriteLine($"  Begrüßung abends: {reflection.Greetings.Evening}");
        output.WriteLine($"  Begrüßung nachts: {reflection.Greetings.Night}");
        var result = 0;
        if (!reflection.Facts.Any(f => f.Contains(ExpectedFact, StringComparison.OrdinalIgnoreCase)))
        {
            output.WriteLine($"FEHLER: \"{ExpectedFact}\" nicht gemerkt.");
            result = 3;
        }
        // Gefragt, aber nicht über sich erzählt – das darf kein Fakt werden.
        if (reflection.Facts.FirstOrDefault(f => f.Contains("Wien", StringComparison.OrdinalIgnoreCase) || f.Contains("Linux", StringComparison.OrdinalIgnoreCase)) is { } guessed)
            output.WriteLine($"WARNUNG: Vermutung als Fakt notiert (\"{guessed}\").");
        if (Forbidden.FirstOrDefault(name => reflection.Facts.Append(reflection.Summary ?? "").Any(f => f.Contains(name, StringComparison.OrdinalIgnoreCase))) is { } leaked)
            output.WriteLine($"WARNUNG: \"{leaked}\" in den Notizen.");
        output.WriteLine();

        // Nächster Start: Begrüßung als erster Satz, dann die Frage.
        var memory = reflection.ApplyTo(MemoryData.Empty, now);
        backend.UpdateSystemPrompt(promptWith(memory));
        var next = new Conversation();
        if (reflection.Greetings.For(now) is { } greeting)
        {
            next.AddAssistant(greeting);
            output.WriteLine($"◆ {greeting}");
        }
        const string question = "Was weißt du eigentlich über mich?";
        next.AddUser(question);
        output.WriteLine($"› {question}");
        var reply = "";
        await foreach (var chunk in backend.StreamReplyAsync(next, CancellationToken.None))
            if (!chunk.IsThinking)
                reply += chunk.Text;
        await backend.CompleteAsync();
        output.WriteLine($"◆ {reply}");
        output.WriteLine();
        if (!reply.Contains(ExpectedFact, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"FEHLER: Max weiß im neuen Gespräch nichts von \"{ExpectedFact}\".");
            result = 3;
        }
        return result;
    }

    /// <summary>"121.932.631.112.635.269" = "121932631112635269".</summary>
    private static string Compact(string text) => new(text.Where(c => c is not ('.' or ' ' or '\u202f' or '\u00a0' or '\'' or '’')).ToArray());

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
