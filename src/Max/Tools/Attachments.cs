using System.Text.RegularExpressions;

namespace Max.Tools;

/// <summary>
/// Dateien, die der Nutzer ins Terminal gezogen (oder als Pfad eingefügt) hat. Das Terminal schreibt dann nur
/// den Pfad in die Eingabe – je nach System in Anführungszeichen ("C:\Bilder\a b.png", '/home/x/a b.png'),
/// mit "\ " statt Leerzeichen (/home/x/a\ b.png) oder als file://-Adresse. Max sieht sich solche Dateien an,
/// bevor er antwortet: Bilder mit <c>bild</c>, Aufnahmen mit <c>audio</c>, alles andere mit <c>datei</c>.
/// </summary>
internal static partial class Attachments
{
    /// <summary>Mehr Dateien auf einmal sieht Max sich nicht an.</summary>
    internal const int MaxFiles = 3;

    /// <summary>
    /// Die Aufrufe für die angehängten Dateien – leer, wenn keine darin steht. Was sonst in der Nachricht steht, ist
    /// die Frage dazu: Ein Bild bekommt sie gestellt, in einem langen Dokument sucht Max die passenden Stellen.
    /// </summary>
    public static IReadOnlyList<ToolCall> Calls(string message, string workingDirectory)
    {
        var files = Find(message, workingDirectory, out var rest);
        var question = rest.Length is > 0 and <= 300 ? rest.Replace('|', '/') : "";
        return files.Select(path => new ToolCall(ImageTool.IsImage(path) ? "bild" : AudioDecoder.IsAudio(path) ? "audio" : "datei",
            question.Length > 0 ? $"{path} | {question}" : path)).ToList();
    }

    /// <summary>Alle vorhandenen Dateien in der Nachricht; <paramref name="rest"/> ist der Text ohne sie.</summary>
    public static IReadOnlyList<string> Find(string message, string workingDirectory, out string rest)
    {
        var files = new List<string>();
        var text = PathRegex().Replace(message, match =>
        {
            var raw = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["s"].Success ? match.Groups["s"].Value : match.Groups["b"].Value.Replace("\\ ", " ");
            if (Existing(raw, workingDirectory) is not { } path || files.Count >= MaxFiles)
                return match.Value;
            if (!files.Contains(path))
                files.Add(path);
            return " ";
        });
        rest = SpaceRegex().Replace(text, " ").Trim();
        return files;
    }

    private static string? Existing(string raw, string workingDirectory)
    {
        try
        {
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                raw = uri.LocalPath;
            var path = ToolPaths.Resolve(raw, workingDirectory);
            return File.Exists(path) && ToolPaths.Forbidden(path) is null ? path : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // In Anführungszeichen (alles bis zum Ende), sonst ab einem Pfad-Anfang (C:\, \\, /, ~/, file://) bis zum
    // nächsten Leerzeichen, das nicht mit \ geschützt ist – oder ein relativer Pfad mit Endung ("bilder/a.png",
    // "README.md"), der ab dem Startordner existiert.
    [GeneratedRegex("""
        "(?<q>(?:[A-Za-z]:[\\/]|\\\\|/|~[\\/]|file://)[^"\r\n]+)"
        | '(?<s>(?:[A-Za-z]:[\\/]|\\\\|/|~[\\/]|file://)[^'\r\n]+)'
        | (?<![\w/\\])(?<b>(?:[A-Za-z]:[\\/]|\\\\|/|~[\\/]|file://)(?:\\\ |[^\s"'])+)
        | (?<![\w/\\.])(?<b>[\w.\-]+(?:[\\/][\w.\-]+)*\.[A-Za-z0-9]{1,5})(?![\w/\\])
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
