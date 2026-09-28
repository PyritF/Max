using Max.Chat;
using Max.Llm;

namespace Max.Memory;

/// <summary>
/// Am Ende eines Gesprächs: Max notiert sich neue Fakten über den Nutzer, worum es ging, und schon die
/// Begrüßungen für den nächsten Start. Ein einziger Auftrag an das Modell, ohne Nachdenken, in einer festen
/// Form (per Grammatik erzwungen) – das ist schnell und lässt sich sicher auslesen.
/// Jeder Fakt braucht als Beleg ein wörtliches Zitat, in dem der Nutzer von sich selbst spricht – das prüft Max
/// selbst nach. So bleiben Vermutungen draußen ("fragt nach Wien" ist nicht "wohnt in Wien").
/// </summary>
internal sealed record Reflection(IReadOnlyList<string> Facts, string? Summary, Greetings Greetings)
{
    /// <summary>Mehr neue Fakten pro Gespräch sind fast immer Nacherzählung.</summary>
    internal const int MaxNewFacts = 5;

    /// <summary>Längere Begrüßungen passen nicht zu "ein kurzer Satz" – dann lieber die feste.</summary>
    internal const int MaxGreetingLength = 100;

    /// <summary>Genug Platz für acht Fakten und fünf kurze Zeilen.</summary>
    internal const int MaxTokens = 700;

    /// <summary>Der Auftrag – als Nachricht des Nutzers hinter dem Gespräch; der Nutzer sieht ihn nie.</summary>
    internal const string Instruction = """
        (Interne Aufgabe, nicht Teil des Gesprächs – der Nutzer sieht das nicht.)
        Das Gespräch ist zu Ende. Halte für dich fest:

        FAKTEN: Was der Nutzer in diesem Gespräch ausdrücklich über sich selbst gesagt hat und auch in
        Wochen noch stimmt – Beruf, Projekte, Vorlieben, Gewohnheiten, wichtige Menschen. Hinter jeden Fakt
        kommt als Beleg sein Satz, wörtlich zitiert. Keine Vermutungen: Wer nach Wien fragt, wohnt nicht
        deshalb dort; wer Diagramme will, "mag Diagramme" nicht. Nichts Vorübergehendes (heute müde,
        gerade beschäftigt), nichts aus deinem Kontext (Betriebssystem, Name), nichts, was du schon weißt.
        Schreib die Fakten als knappe Notiz in der dritten Person, ohne Namen.
        Beispiel: - Programmiert in C#. | "Ich programmiere in C#"
        Bitten und Fragen an dich sind keine Fakten ("Erzähl mir was über den Herbst" heißt nicht "mag den Herbst").
        Hat er nichts Dauerhaftes über sich erzählt – das ist oft so –, keine Zeile.
        ZUSAMMENFASSUNG: die Themen des Gesprächs als Stichworte, z. B. "Wien-Tipps, Herbst, Sprachmodelle"
        (höchstens 60 Zeichen) – nur Themen, nichts über ihn.
        MORGEN, TAG, ABEND, NACHT: je eine Begrüßung für seinen nächsten Start zu dieser Tageszeit – du sprichst
        ihn direkt an. Ein kurzer Satz (höchstens 70 Zeichen) in deinem trockenen Ton, der an etwas Konkretes
        aus diesem Gespräch anknüpft, z. B. "Wieder am C#-Code? Der Kaffee ist hoffentlich schon schwarz."
        Ohne "Guten Morgen", "Hallo" oder den Namen – das steht schon da.
        """;

    /// <summary>Die feste Form der Antwort.</summary>
    internal const string Gbnf = """
        root ::= "FAKTEN:\n" fact{0,5} "ZUSAMMENFASSUNG: " line "MORGEN: " line "TAG: " line "ABEND: " line "NACHT: " line
        fact ::= "- " [^\n|"]{2,120} " | \"" [^\n"]{2,160} "\"\n"
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
    /// <param name="userSaid">Was der Nutzer geschrieben hat – nur Fakten mit einem Zitat daraus bleiben.</param>
    internal static Reflection? Parse(string text, DateTime now, IEnumerable<string> userSaid)
    {
        var said = MemoryData.Normalize(string.Join(" \n ", userSaid));
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
                if (summary is null && facts.Count < MaxNewFacts && Fact(line[2..], said) is { } fact)
                    facts.Add(fact);
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
        var reflection = Parse(text, now, messages.Where(m => m.Role == ChatRole.User).Select(m => m.Content));
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

    /// <summary>
    /// "Programmiert in C#. | "Ich programmiere in C#"" → der Fakt, wenn das Zitat belegt, dass der Nutzer von sich
    /// selbst sprach: wörtlich (bis auf Satzzeichen) aus seinen Nachrichten, mit ich/mein/mir/mich, nicht von heute.
    /// </summary>
    internal static string? Fact(string line, string said)
    {
        var bar = line.IndexOf(" | ", StringComparison.Ordinal);
        if (bar < 0)
            return null;
        var fact = line[..bar].Trim();
        var quote = MemoryData.Normalize(line[(bar + 3)..]);
        if (IsNothing(fact) || quote.Length < 5 || !said.Contains(quote, StringComparison.Ordinal))
        {
            LlmEngine.Log($"Gedächtnis: ohne Beleg verworfen: {line}");
            return null;
        }
        var words = quote.Split(' ');
        if (!words.Any(w => w is "ich" or "mein" or "meine" or "meinen" or "meinem" or "meiner" or "mir" or "mich")
            || words.Any(w => w is "heute" or "gestern" or "vorhin")
            || IsRequest(line[(bar + 3)..], words))
        {
            LlmEngine.Log($"Gedächtnis: nicht über sich oder nur vorübergehend, verworfen: {line}");
            return null;
        }
        return fact;
    }

    /// <summary>
    /// Bitten und Fragen an Max sind keine Aussagen über sich – "Erzähl mir was über den Herbst" heißt nicht "mag den Herbst".
    /// </summary>
    private static bool IsRequest(string rawQuote, string[] words) =>
        rawQuote.Trim().TrimEnd('"').TrimEnd().EndsWith('?')
        || words is [var first, ..] && Requests.Contains(first)
        || words.Any(w => w is "du" or "dir" or "dich" or "kannst" or "könntest" or "bitte");

    private static readonly HashSet<string> Requests =
    [
        "zeig", "zeige", "erzähl", "erzähle", "erklär", "erkläre", "gib", "schreib", "schreibe", "mach", "mache",
        "sag", "sage", "hilf", "nenn", "nenne", "such", "suche", "rechne", "übersetz", "übersetze", "fass", "lass",
    ];

    /// <summary>Kleine Modelle schreiben "keine" statt gar keiner Zeile.</summary>
    private static bool IsNothing(string fact) =>
        MemoryData.Normalize(fact) is "keine" or "keine neuen fakten" or "nichts" or "nichts neues" or "keine fakten";

    private static string Unquote(string text) => text.Trim().Trim('"', '„', '“', '”').Trim();
}
