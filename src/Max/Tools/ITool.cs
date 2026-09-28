namespace Max.Tools;

/// <summary>
/// Ein Werkzeug, das Max während einer Antwort benutzen kann. Bewusst schlicht: ein Name und höchstens ein
/// Text als Angabe (Suchbegriff, Pfad, Rechnung). Alle Werkzeuge lesen nur – keins verändert etwas.
/// </summary>
internal interface ITool
{
    /// <summary>So ruft das Modell es auf, z. B. "websuche".</summary>
    string Name { get; }

    /// <summary>Was die Angabe ist ("Suchbegriff") – null, wenn das Werkzeug keine braucht.</summary>
    string? Argument { get; }

    /// <summary>Für den System-Prompt: wann das Werkzeug hilft.</summary>
    string Description { get; }

    /// <summary>Für die Anzeige, während es läuft: "Suche im Web: Einwohner Wien".</summary>
    string Describe(string argument);

    /// <summary>Das Ergebnis als Text für das Modell. Fehler als verständlicher Satz, nicht als Ausnahme.</summary>
    Task<string> RunAsync(string argument, CancellationToken ct);
}

/// <summary>Ein Aufruf, wie ihn das Modell geschrieben hat.</summary>
internal sealed record ToolCall(string Name, string Argument)
{
    /// <summary>Der Name des Blocks, mit dem das Modell ein Werkzeug aufruft: <c>```werkzeug</c>.</summary>
    public const string BlockName = "werkzeug";

    /// <summary>Ein Code-Block am Antwortanfang, der ein Aufruf sein könnte ("```python" / "datei: README.md").</summary>
    public const string MaybeBlockName = "werkzeug?";

    /// <summary>So steht der Aufruf im Verlauf – genau wie das Modell ihn schreibt.</summary>
    public string Text => $"```{BlockName}\n{Name}{(Argument.Length > 0 ? ": " + Argument : "")}\n```";

    /// <summary>"websuche: Einwohner Wien" → Aufruf. Null, wenn die Zeile keinen Werkzeugnamen hat.</summary>
    public static ToolCall? Parse(string body)
    {
        // "werkzeug" als eigene erste Zeile (im Code-Block "```bash" / "werkzeug" / "websuche: …") überspringen.
        var line = body.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.Equals(BlockName, StringComparison.OrdinalIgnoreCase));
        if (line is null)
            return null;
        var colon = line.IndexOf(':');
        return colon < 0
            ? new ToolCall(line.ToLowerInvariant(), "")
            : new ToolCall(line[..colon].Trim().ToLowerInvariant(), line[(colon + 1)..].Trim().Trim('"', '\'', '`').Trim());
    }
}
