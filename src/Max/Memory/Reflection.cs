using Max.Chat;
using Max.Llm;

namespace Max.Memory;

/// <summary>
/// Am Ende eines Gesprächs: Max notiert sich neue Fakten über den Nutzer, worum es ging, und schon die
/// Begrüßungen für den nächsten Start. Ein einziger Auftrag an das Modell, ohne Nachdenken, in einer festen
/// Form (per Grammatik erzwungen) – das ist schnell und lässt sich sicher auslesen.
/// </summary>
internal sealed record Reflection(IReadOnlyList<string> Facts, string? Summary, Greetings Greetings)
{
    /// <summary>Mehr neue Fakten pro Gespräch sind fast immer Nacherzählung.</summary>
    internal const int MaxNewFacts = 8;

    /// <summary>Längere Begrüßungen passen nicht zu "ein kurzer Satz" – dann lieber die feste.</summary>
    internal const int MaxGreetingLength = 100;

    /// <summary>Genug Platz für acht Fakten und fünf kurze Zeilen.</summary>
    internal const int MaxTokens = 700;

    /// <summary>Der Auftrag – als Nachricht des Nutzers hinter dem Gespräch; der Nutzer sieht ihn nie.</summary>
    internal const string Instruction = """
        (Interne Aufgabe, nicht Teil des Gesprächs – der Nutzer sieht das nicht.)
        Das Gespräch ist zu Ende. Halte für dich fest:

        FAKTEN: neue, dauerhafte Fakten über den Nutzer aus diesem Gespräch – zum Beispiel Beruf, Projekte,
        Vorlieben, Gewohnheiten, wichtige Menschen. Nur, was er selbst über sich gesagt hat. Nichts
        Vorübergehendes ("ist heute müde"), nichts, was du schon über ihn weißt, nichts über dich selbst.
        Je ein kurzer Satz ohne Namen, z. B. "- Programmiert in C#." Gibt es nichts Neues, keine Zeile.
        ZUSAMMENFASSUNG: worum es ging, in wenigen Worten (höchstens 60 Zeichen).
        MORGEN, TAG, ABEND, NACHT: je eine Begrüßung für seinen nächsten Start zu dieser Tageszeit.
        Ein kurzer Satz (höchstens 70 Zeichen) in deinem Ton, gern mit Bezug auf dieses Gespräch.
        Ohne "Guten Morgen", "Hallo" oder den Namen – das steht schon da.
        """;

    /// <summary>Die feste Form der Antwort.</summary>
    internal const string Gbnf = """
        root ::= "FAKTEN:\n" fact{0,8} "ZUSAMMENFASSUNG: " line "MORGEN: " line "TAG: " line "ABEND: " line "NACHT: " line
        fact ::= "- " line
        line ::= [^\n]{2,160} "\n"
        """;

    private static readonly (string Key, int Index)[] GreetingKeys = [("MORGEN", 0), ("TAG", 1), ("ABEND", 2), ("NACHT", 3)];

    /// <summary>Fertig, sobald die letzte Zeile steht – dann muss das Modell nicht noch ein Ende-Token finden.</summary>
    internal static bool IsComplete(string text)
    {
        var night = text.LastIndexOf("\nNACHT: ", StringComparison.Ordinal);
        return night >= 0 && text.IndexOf('\n', night + 1) >= 0;
    }

    /// <summary>Liest die Antwort des Modells. Null, wenn sie nicht die erwartete Form hat.</summary>
    internal static Reflection? Parse(string text, DateTime now)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "FAKTEN:")
            return null;

        var facts = new List<string>();
        string? summary = null;
        var greetings = new string?[4];
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (summary is null && facts.Count < MaxNewFacts && !IsNothing(line[2..]))
                    facts.Add(line[2..].Trim());
                continue;
            }
            if (Value(line, "ZUSAMMENFASSUNG") is { } s)
                summary = s;
            foreach (var (key, index) in GreetingKeys)
                if (Value(line, key) is { } g && Unquote(g) is { Length: <= MaxGreetingLength } greeting)
                    greetings[index] = greeting;
        }
        if (summary is null)
            return null;
        return new Reflection(facts, summary, new Greetings(greetings[0], greetings[1], greetings[2], greetings[3], now));
    }

    /// <summary>Lässt das Modell nachdenken und liest das Ergebnis. Null, wenn es nichts Brauchbares lieferte.</summary>
    public static async Task<Reflection?> RunAsync(LlmBackend backend, IReadOnlyList<ChatMessage> messages, DateTime now, CancellationToken ct)
    {
        var text = await backend.RunTaskAsync(messages, Instruction, Gbnf, MaxTokens, IsComplete, ct);
        var reflection = Parse(text, now);
        LlmEngine.Log(reflection is null ? $"Gedächtnis: Antwort nicht lesbar: {text.ReplaceLineEndings(" / ")}" : $"Gedächtnis: {text.ReplaceLineEndings(" / ")}");
        return reflection;
    }

    /// <summary>Überträgt das Ergebnis ins Gedächtnis.</summary>
    public MemoryData ApplyTo(MemoryData memory, DateTime now)
    {
        var updated = memory.WithFacts(Facts, DateOnly.FromDateTime(now));
        return updated with
        {
            LastSession = Summary is { Length: > 0 } s ? new LastSession(now, s) : updated.LastSession,
            NextGreeting = Greetings,
        };
    }

    private static string? Value(string line, string key) =>
        line.StartsWith(key + ":", StringComparison.Ordinal) && line[(key.Length + 1)..].Trim() is { Length: > 0 } value ? value : null;

    /// <summary>Kleine Modelle schreiben "keine" statt gar keiner Zeile.</summary>
    private static bool IsNothing(string fact) =>
        MemoryData.Normalize(fact) is "keine" or "keine neuen fakten" or "nichts" or "nichts neues" or "keine fakten";

    private static string Unquote(string text) => text.Trim().Trim('"', '„', '“', '”').Trim();
}
