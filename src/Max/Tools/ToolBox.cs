using System.Diagnostics;
using System.Text;
using Max.Llm;

namespace Max.Tools;

/// <summary>
/// Alle Werkzeuge, die Max hat: Beschreibung für den System-Prompt, Grammatik für den Aufruf und das Ausführen.
/// Ergebnisse werden gekürzt, damit eine große Datei oder Webseite nicht den ganzen Kontext füllt.
/// </summary>
internal sealed class ToolBox(IEnumerable<ITool> tools)
{
    /// <summary>So viele Zeichen eines Ergebnisses sieht das Modell höchstens (etwa 2.500 Tokens).</summary>
    internal const int MaxResultChars = 8000;

    /// <summary>Länger darf ein Werkzeug nicht brauchen – sonst wartet der Nutzer ewig.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ITool> All => _tools.Values;

    public ITool? Find(string name) => _tools.GetValueOrDefault(name);

    /// <summary>Die Standard-Werkzeuge – alle nur lesend.</summary>
    public static ToolBox CreateDefault(HttpClient web, Func<DateTime> clock, Func<string> workingDirectory) => new(
    [
        new ClockTool(clock),
        new SystemInfoTool(),
        new CalculatorTool(),
        new ListFolderTool(workingDirectory),
        new ReadFileTool(workingDirectory),
        new WebSearchTool(web),
        new ReadWebPageTool(web),
    ]);

    /// <summary>
    /// GBNF: der Inhalt eines <c>```werkzeug</c>-Blocks – genau ein Aufruf, ein bekannter Name, bei Bedarf die Angabe.
    /// </summary>
    public string GrammarRule()
    {
        var calls = _tools.Values.Select(t => t.Argument is null
            ? $"\"{t.Name}\""
            : $"\"{t.Name}: \" [^\\n`]{{1,300}}");
        return $"\"{ToolCall.BlockName}\" \"\\n\" ( {string.Join(" | ", calls)} ) \"\\n```\"";
    }

    /// <summary>Die Werkzeug-Liste für den System-Prompt.</summary>
    public string PromptList()
    {
        var text = new StringBuilder();
        foreach (var tool in _tools.Values)
        {
            var call = tool.Argument is null ? tool.Name : $"{tool.Name}: <{tool.Argument}>";
            text.Append("- `").Append(call).Append("` – ").Append(tool.Description).Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Führt den Aufruf aus. Fehler, Zeitüberschreitung und Unbekanntes werden zu einem Satz fürs Modell.</summary>
    public async Task<string> RunAsync(ToolCall call, CancellationToken ct)
    {
        if (Find(call.Name) is not { } tool)
            return $"Ein Werkzeug \"{call.Name}\" gibt es nicht.";
        if (tool.Argument is not null && call.Argument.Length == 0)
            return $"{tool.Name} braucht eine Angabe ({tool.Argument}).";

        var clock = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        string result;
        try
        {
            result = await tool.RunAsync(call.Argument, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = $"Abgebrochen – {tool.Name} hat länger als {Timeout.TotalSeconds:0} Sekunden gebraucht.";
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
