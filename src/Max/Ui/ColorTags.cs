using Spectre.Console;

namespace Max.Ui;

/// <summary>
/// Die Farben, die Max in Antworten per <c>{rot}…{/rot}</c> setzen darf. Eigene geschweifte Tags statt
/// Spectre-Markup, damit eckige Klammern in Code (<c>arr[0]</c>) nie etwas kaputt machen.
/// Ausgewertet werden sie im <see cref="Markdown.InlineFormatter"/>.
/// </summary>
internal static class ColorTags
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
        ["akzent"] = Theme.Accent,
    };

    public static bool TryGet(string name, out Color color) => Colors.TryGetValue(name, out color);
}
