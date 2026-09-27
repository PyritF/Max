using System.Text.RegularExpressions;
using Spectre.Console;

namespace Max.Ui;

/// <summary>
/// Die Farben, die Max in Antworten per <c>{rot}…{/rot}</c> setzen darf. Eigene geschweifte Tags statt
/// Spectre-Markup, damit eckige Klammern in Code (<c>arr[0]</c>) nie etwas kaputt machen.
/// Ausgewertet werden sie im <see cref="Markdown.InlineFormatter"/>.
/// </summary>
internal static partial class ColorTags
{
    private static readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rot"] = new Color(240, 80, 80),
        ["grün"] = new Color(90, 200, 120),
        ["gruen"] = new Color(90, 200, 120),
        ["gelb"] = new Color(230, 200, 80),
        ["blau"] = new Color(100, 150, 240),
        ["cyan"] = new Color(80, 200, 210),
        ["magenta"] = new Color(210, 110, 210),
        ["grau"] = Theme.Muted,
        ["orange"] = new Color(255, 150, 40),
        ["pink"] = new Color(255, 105, 180),
        ["rosa"] = new Color(255, 160, 200),
        ["lila"] = new Color(170, 110, 230),
        ["violett"] = new Color(170, 110, 230),
        ["türkis"] = new Color(64, 224, 208),
        ["weiß"] = new Color(240, 240, 240),
        ["weiss"] = new Color(240, 240, 240),
        ["gold"] = new Color(230, 190, 60),
        ["akzent"] = Theme.Accent,
    };

    /// <summary>Alle erlaubten Namen (für die Grammatik, die das Modell auf gültige Tags festlegt).</summary>
    public static IEnumerable<string> Names => Colors.Keys;

    public static bool TryGet(string name, out Color color) => Colors.TryGetValue(name, out color);

    /// <summary>
    /// Entfernt Farb- und Verlaufs-Tags, der Text bleibt. Code (Blöcke und `…`) und unbekannte Klammern
    /// wie <c>{name}</c> bleiben unverändert.
    /// </summary>
    public static string Strip(string text)
    {
        var lines = text.Split('\n');
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                inFence = !inFence;
            else if (!inFence)
                lines[i] = TagRegex().Replace(lines[i], m => IsTag(m) ? "" : m.Value);
        }
        return string.Join('\n', lines);
    }

    private static bool IsTag(Match m)
    {
        if (!m.Groups["name"].Success)
            return false;                               // Inline-Code
        var name = m.Groups["name"].Value;
        return name.StartsWith("verlauf", StringComparison.OrdinalIgnoreCase) || Colors.ContainsKey(name);
    }

    [GeneratedRegex(@"`[^`\n]*`|[{\[]/?(?<name>verlauf[^}\]\n]*|[\p{L}]+)[}\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();
}
