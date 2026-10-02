using System.Text.RegularExpressions;
using Max.Llm;

namespace Max.Tools;

/// <summary>
/// Sieht in die Zwischenablage: ein Screenshot (Win+Umschalt+S) geht an den Bild-Zusatz, Text kommt als Text,
/// kopierte Dateien als Liste. Nur auf ausdrücklichen Wunsch – in der Zwischenablage liegen oft auch Passwörter;
/// das Modell darf sie nicht von sich aus (oder auf Anweisung einer Webseite) auslesen.
/// </summary>
internal sealed partial class ClipboardTool(IClipboard clipboard, Func<IVision?> vision) : ITool
{
    internal const string DefaultQuestion =
        "Das ist ein Screenshot. Beschreibe kurz, was zu sehen ist, und schreib allen sichtbaren Text wörtlich ab – " +
        "Fehlermeldungen vollständig. Antworte auf Deutsch.";

    public string Name => "zwischenablage";
    public string? Argument => "Frage zum Bild darin";
    public bool ArgumentOptional => true;
    public string Description => "Was in der Zwischenablage liegt: ein Screenshot (wird angesehen), kopierter Text oder kopierte Dateien. Nur, wenn der Nutzer danach fragt.";
    public string Describe(string argument) => "Sehe in die Zwischenablage";

    // Ein Screenshot auf der CPU dauert gut eine Minute.
    public TimeSpan Timeout => TimeSpan.FromMinutes(5);

    /// <summary>Nur wenn der Nutzer in seiner Nachricht die Zwischenablage oder einen Screenshot erwähnt.</summary>
    public bool AllowedFor(string? request) => request is not null && Mentioned(request);

    internal static bool Mentioned(string message) => MentionRegex().IsMatch(message);

    public async Task<string> RunAsync(string argument, CancellationToken ct)
    {
        ClipboardContent content;
        try
        {
            content = await Task.Run(clipboard.Read, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException)
        {
            return e.Message;
        }
        if (content.IsEmpty)
            return "Die Zwischenablage ist leer.";
        if (content.Files.Count > 0)
            return "In der Zwischenablage liegen kopierte Dateien:\n" + string.Join('\n', content.Files.Select(f => "- " + f)) +
                   "\n(Ansehen mit `datei: <Pfad>`, Bilder mit `bild: <Pfad>`.)";
        if (content.Image is { } image)
        {
            if (vision() is not { } eyes)
                return "In der Zwischenablage liegt ein Bild – aber das Bildverständnis ist noch nicht auf diesem Rechner (es wird im Hintergrund geladen).";
            var file = Path.Combine(Path.GetTempPath(), $"max-zwischenablage-{Guid.NewGuid():N}{content.ImageExtension}");
            try
            {
                await File.WriteAllBytesAsync(file, image, ct);
                var question = argument.Trim().Length > 0 ? argument.Trim() + " Antworte auf Deutsch." : DefaultQuestion;
                var answer = await eyes.LookAsync(file, question, ct);
                return answer.Length == 0 ? "Zum Bild in der Zwischenablage kam keine Beschreibung heraus." : $"Bild aus der Zwischenablage:\n{answer}";
            }
            finally
            {
                File.Delete(file);
            }
        }
        return $"Text in der Zwischenablage:\n{content.Text}";
    }

    [GeneratedRegex(@"\b(zwischenablage|clipboard|screenshots?|bildschirmfotos?|bildschirmausschnitts?|bildschirmaufnahme)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MentionRegex();
}
