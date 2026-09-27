using System.Globalization;
using Max.Setup;
using Max.Ui;

namespace Max.Persona;

/// <summary>Max' System-Prompt: fest in die Exe eingebaut, Platzhalter werden beim Start gefüllt.</summary>
internal static class SystemPrompt
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Build(SystemSnapshot system) => BuildParts(system).Text;

    /// <summary>
    /// Der Prompt samt Länge des festen Anfangs: Alles vor der ersten Zeile mit Datum, Uhrzeit oder Name
    /// ist bei jedem Start gleich – diesen Teil kann Max gerechnet auf der Platte aufheben (<see cref="Llm.PromptCache"/>).
    /// </summary>
    public static BuiltPrompt BuildParts(SystemSnapshot system)
    {
        var template = LoadTemplate().ReplaceLineEndings("\n");
        var text = Fill(template, system);
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
    internal static string Fill(string template, SystemSnapshot system) => template
        .Replace("{{datum}}", system.Now.ToString("dddd, d. MMMM yyyy", German))
        .Replace("{{uhrzeit}}", system.Now.ToString("HH:mm", German))
        .Replace("{{os}}", system.OsName)
        .Replace("{{name}}", system.User.FullName)
        .Replace("{{vorname}}", system.User.FirstName)
        .ReplaceLineEndings("\n")
        .Trim();

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
