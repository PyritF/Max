using System.Diagnostics;
using System.Text;
using Max.Llm;

namespace Max.Tools;

/// <summary>
/// Alle Werkzeuge, die Max hat: Beschreibung für den System-Prompt, Grammatik für den Aufruf und das Ausführen.
/// Ergebnisse werden gekürzt, damit eine große Datei oder Webseite nicht den ganzen Kontext füllt.
/// </summary>
internal sealed partial class ToolBox(IEnumerable<ITool> tools, Func<string>? workingDirectory = null)
{
    /// <summary>Hier wurde Max gestartet – relative Pfade gelten ab hier.</summary>
    public Func<string> WorkingDirectory { get; } = workingDirectory ?? (() => Environment.CurrentDirectory);

    /// <summary>So viele Zeichen eines Ergebnisses sieht das Modell höchstens (etwa 2.500 Tokens).</summary>
    internal const int MaxResultChars = 8000;

    /// <summary>Länger darf ein Werkzeug nicht brauchen – sonst wartet der Nutzer ewig.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ITool> All => _tools.Values;

    public ITool? Find(string name) => _tools.GetValueOrDefault(name);

    /// <summary>Die Standard-Werkzeuge – alle nur lesend.</summary>
    /// <param name="vision">Bildverständnis, sobald es bereitsteht (sonst null) – ohne Angabe gibt es kein <c>bild</c>.</param>
    public static ToolBox CreateDefault(HttpClient web, Func<DateTime> clock, Func<string> workingDirectory, Func<IVision?>? vision = null) => new(
    [
        new ClockTool(clock),
        new SystemInfoTool(),
        new CalculatorTool(),
        new ListFolderTool(workingDirectory),
        new ReadFileTool(workingDirectory, vision),
        new FindFilesTool(workingDirectory),
        .. vision is null ? Array.Empty<ITool>() : [new ImageTool(vision, workingDirectory)],
        new WebSearchTool(web),
        new ReadWebPageTool(web),
        new WeatherTool(web),
        new ClipboardTool(new SystemClipboard(), vision ?? (() => null)),
    ], workingDirectory);

    /// <summary>
    /// Was Max sich ansieht, bevor er antwortet: Dateien, die der Nutzer in die Nachricht gezogen hat – oder, wenn er
    /// ausdrücklich von der Zwischenablage oder einem Screenshot spricht, die Zwischenablage (nur Werkzeuge, die es gibt).
    /// </summary>
    public IReadOnlyList<ToolCall> AttachmentCalls(string message)
    {
        var calls = Attachments.Calls(message, WorkingDirectory()).Where(call => Find(call.Name) is not null).ToList();
        if (calls.Count == 0 && Find("zwischenablage") is not null && ClipboardTool.Mentioned(message))
            calls.Add(new ToolCall("zwischenablage", message.Length <= 300 ? message.Replace('|', '/').ReplaceLineEndings(" ").Trim() : ""));
        return calls;
    }

    /// <summary>
    /// Welches Werkzeug Max beim Nachdenken benutzen will ("Ich sollte das Werkzeug `rechnen` verwenden", "Ich rufe
    /// `websuche` auf") – oder null. Zählt nur ein Satz mit Werkzeug-Namen (in Backticks oder nach "Werkzeug") und
    /// einem Verb des Benutzens, ohne Verneinung: "Ich habe `rechnen` für große Zahlen" ist keine Absicht.
    /// </summary>
    public string? IntendedTool(string thought)
    {
        foreach (var sentence in SentenceRegex().Split(thought))
        {
            if (!IntentRegex().IsMatch(sentence) || NegationRegex().IsMatch(sentence))
                continue;
            foreach (var tool in _tools.Values)
            {
                var name = System.Text.RegularExpressions.Regex.Escape(tool.Name);
                if (System.Text.RegularExpressions.Regex.IsMatch(sentence, $@"`{name}(:[^`]*)?`|\bWerkzeug\s+[""„]?{name}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return tool.Name;
            }
        }
        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial System.Text.RegularExpressions.Regex SentenceRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(verwende|verwenden|benutze|benutzen|nutze|nutzen|rufe|aufrufen|aufzurufen|einsetzen|starte|starten|brauche)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex IntentRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(nicht|kein\w*|ohne|statt)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex NegationRegex();

    /// <summary>
    /// GBNF: der Inhalt eines <c>```werkzeug</c>-Blocks – genau ein Aufruf, ein bekannter Name, bei Bedarf die Angabe.
    /// </summary>
    public string GrammarRule()
    {
        var calls = _tools.Values.Select(t => t.Argument is null
            ? $"\"{t.Name}\""
            : t.ArgumentOptional
                ? $"\"{t.Name}\" ( \": \" [^\\n`]{{1,300}} )?"
                : $"\"{t.Name}: \" [^\\n`]{{1,300}}");
        return $"\"{ToolCall.BlockName}\" \"\\n\" ( {string.Join(" | ", calls)} ) \"\\n```\"";
    }

    /// <summary>Die Werkzeug-Liste für den System-Prompt.</summary>
    public string PromptList()
    {
        var text = new StringBuilder();
        foreach (var tool in _tools.Values)
        {
            var call = tool.Argument is null ? tool.Name
                : tool.ArgumentOptional ? $"{tool.Name}` oder `{tool.Name}: <{tool.Argument}>"
                : $"{tool.Name}: <{tool.Argument}>";
            text.Append("- `").Append(call).Append("` – ").Append(tool.Description).Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Führt den Aufruf aus. Fehler, Zeitüberschreitung und Unbekanntes werden zu einem Satz fürs Modell.</summary>
    /// <param name="request">Die Nachricht des Nutzers, auf die Max gerade antwortet – manche Werkzeuge nur auf seinen Wunsch.</param>
    public async Task<string> RunAsync(ToolCall call, CancellationToken ct, string? request = null)
    {
        if (Find(call.Name) is not { } tool)
            return $"Ein Werkzeug \"{call.Name}\" gibt es nicht.";
        if (tool.Argument is not null && !tool.ArgumentOptional && call.Argument.Length == 0)
            return $"{tool.Name} braucht eine Angabe ({tool.Argument}).";
        if (!tool.AllowedFor(request))
        {
            LlmEngine.Log($"Werkzeug {call.Name} ohne ausdrücklichen Wunsch des Nutzers verweigert.");
            return $"Nicht ausgeführt: {tool.Name} nur, wenn der Nutzer in seiner Nachricht ausdrücklich danach fragt.";
        }

        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(tool.Timeout);
        string result;
        try
        {
            result = await tool.RunAsync(call.Argument, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = $"Abgebrochen – {tool.Name} hat länger als {tool.Timeout.TotalSeconds:0} Sekunden gebraucht.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result = $"Fehler: {e.Message}";
        }
        LlmEngine.Log($"Werkzeug {call.Name}({call.Argument}) in {clock.Elapsed.TotalSeconds:0.0} s: {Shorten(result, 300).ReplaceLineEndings(" / ")}");
        return Shorten(result, MaxResultChars);
    }

    internal static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + $"\n… (gekürzt, {text.Length - max} Zeichen mehr)";
}
