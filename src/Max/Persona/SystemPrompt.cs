using System.Globalization;
using System.Text;
using Max.Memory;
using Max.Ui;

namespace Max.Persona;

/// <summary>Max' System-Prompt: fest in die Exe eingebaut, Platzhalter werden beim Start gefüllt.</summary>
internal static class SystemPrompt
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Build(SystemSnapshot system, MemoryData? memory = null, Tools.ToolBox? tools = null) => BuildParts(system, memory, tools).Text;

    /// <summary>
    /// Der Prompt samt Länge des festen Anfangs: Alles vor der ersten Zeile mit Datum, Uhrzeit oder Name
    /// ist bei jedem Start gleich – diesen Teil kann Max gerechnet auf der Platte aufheben (<see cref="Llm.PromptCache"/>).
    /// </summary>
    public static BuiltPrompt BuildParts(SystemSnapshot system, MemoryData? memory = null, Tools.ToolBox? tools = null)
    {
        // Die Werkzeug-Liste gehört zum festen Teil – sie wird vor allem anderen eingesetzt.
        var template = LoadTemplate().ReplaceLineEndings("\n").Replace("{{werkzeuge}}", ToolPrompt.Section(tools).ReplaceLineEndings("\n"));
        var text = Fill(template, system, memory);
        var stable = template[..VariableStart(template)];
        return new BuiltPrompt(text, text.StartsWith(stable, StringComparison.Ordinal) ? stable.Length : 0);
    }

    /// <summary>Beginn der ersten Zeile mit einem Platzhalter.</summary>
    internal static int VariableStart(string template)
    {
        var placeholder = template.IndexOf("{{", StringComparison.Ordinal);
        return placeholder < 0 ? template.Length : template.LastIndexOf('\n', placeholder) + 1;
    }

    /// <summary>
    /// Die Uhrzeit ist bewusst die vom Gesprächsbeginn: Ändert sich der System-Prompt, müsste das
    /// Modell den ganzen Verlauf neu lesen.
    /// </summary>
    internal static string Fill(string template, SystemSnapshot system, MemoryData? memory = null) => template
        .Replace("{{datum}}", system.Now.ToString("dddd, d. MMMM yyyy", German))
        .Replace("{{uhrzeit}}", system.Now.ToString("HH:mm", German))
        .Replace("{{os}}", system.OsName)
        .Replace("{{name}}", system.User.FullName)
        .Replace("{{vorname}}", system.User.FirstName)
        .Replace("{{gedaechtnis}}", MemorySection(memory, system.Now))
        .ReplaceLineEndings("\n")
        .Trim();

    /// <summary>
    /// Was Max aus früheren Gesprächen weiß – ganz am Ende, damit der feste Anfang des Prompts gleich bleibt
    /// und sich beim Vergessen nur wenig neu rechnen muss.
    /// </summary>
    internal static string MemorySection(MemoryData? memory, DateTime now)
    {
        if (memory is null || memory.Facts.Count == 0 && memory.LastSession is null)
            return "";
        var text = new StringBuilder("\n## Was du über den Nutzer weißt\n");
        text.Append("Aus früheren Gesprächen, von dir selbst notiert. Nutze es, wo es natürlich passt – zähl es nicht auf ");
        text.Append("und sag nicht, woher du es weißt. Fragt er, was du über ihn weißt, antworte ehrlich und nur daraus – ");
        text.Append("erfinde nichts dazu. Nur wenn er fragt, wie er etwas löscht: mit /vergiss.\n");
        foreach (var fact in memory.Facts)
            text.Append("- ").Append(fact.Text).Append('\n');
        if (memory.LastSession is { } last)
            text.Append("- Themen des letzten Gesprächs (").Append(last.Ended.ToString("dddd, d. MMMM", German)).Append("): ").Append(last.Summary)
                .Append(" – worüber ihr gesprochen habt, keine Fakten über ihn.\n");
        return text.ToString();
    }

    internal static string LoadTemplate() => Load("system-prompt.md");

    private static string Load(string name)
    {
        using var stream = typeof(SystemPrompt).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} fehlt in der Exe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <param name="Text">Der fertige System-Prompt.</param>
/// <param name="StableLength">So viele Zeichen am Anfang sind bei jedem Start gleich.</param>
internal sealed record BuiltPrompt(string Text, int StableLength);
