using System.Globalization;
using Max.Ui;

namespace Max.Persona;

/// <summary>Max' System-Prompt: fest in die Exe eingebaut, Platzhalter werden beim Start gefüllt.</summary>
internal static class SystemPrompt
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Build(SystemSnapshot system) => Fill(LoadTemplate(), system);

    /// <summary>
    /// Die Uhrzeit ist bewusst die vom Gesprächsbeginn: Ändert sich der System-Prompt, müsste das
    /// Modell den ganzen Verlauf neu lesen.
    /// </summary>
    internal static string Fill(string template, SystemSnapshot system) => template
        .Replace("{{datum}}", system.Now.ToString("dddd, d. MMMM yyyy", German))
        .Replace("{{uhrzeit}}", system.Now.ToString("HH:mm", German))
        .Replace("{{os}}", system.OsName)
        .Replace("{{benutzername}}", system.UserName)
        .ReplaceLineEndings("\n")
        .Trim();

    internal static string LoadTemplate()
    {
        using var stream = typeof(SystemPrompt).Assembly.GetManifestResourceStream("system-prompt.md")
            ?? throw new InvalidOperationException("system-prompt.md fehlt in der Exe.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
