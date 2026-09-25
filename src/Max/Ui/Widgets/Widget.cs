using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Max.Ui.Widgets;

/// <summary>
/// Ein Darstellungs-Baustein, den Max per Code-Block einsetzt, z. B. <c>```balken</c>.
/// Kann der Inhalt nicht gedeutet werden, liefert <see cref="Render"/> null – dann erscheint
/// der Block einfach als Code. Fehlermeldungen mitten in der Antwort gibt es nie.
/// </summary>
internal interface IWidget
{
    string Name { get; }
    IRenderable? Render(WidgetBody body, int width);
}

/// <summary>Kennt alle Widgets und zeichnet sie.</summary>
internal static class WidgetRegistry
{
    private static readonly Dictionary<string, IWidget> All = new IWidget[]
    {
        new BarWidget(), new BreakdownWidget(), new LineChartWidget(), new ProgressWidget(),
        new TreeWidget(), new BoxWidget(), new ColumnsWidget(), new CalendarWidget(), new TitleWidget(),
    }.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);

    public static bool IsWidget(string name) => All.ContainsKey(name);

    public static IEnumerable<string> Names => All.Keys;

    /// <summary>Zeichnet das Widget oder liefert null, wenn es nicht klappt.</summary>
    public static IRenderable? TryRender(string name, string body, int width)
    {
        if (!All.TryGetValue(name, out var widget))
            return null;
        try
        {
            return widget.Render(new WidgetBody(body), Math.Max(20, width));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Der Inhalt eines Widget-Blocks: Zeilen, "Schlüssel: Wert"-Paare, Zahlen.</summary>
internal sealed class WidgetBody(string text)
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Schlüssel, die Einstellungen sind und keine Daten.</summary>
    private static readonly HashSet<string> SettingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Titel", "Title", "Farbe", "Color", "Schrift", "Font", "Verlauf", "Text", "Monat", "Markiert", "Einheit",
    };

    public string Text { get; } = text.ReplaceLineEndings("\n").Trim('\n');

    public IReadOnlyList<string> Lines => Text.Split('\n');

    public string? Setting(string key) =>
        Pairs(includeSettings: true).FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>
    /// Alle "Schlüssel: Wert"-Zeilen (Aufzählungszeichen davor werden ignoriert).
    /// Auch Tabellen-Zeilen wie "Zeitpunkt: 0 | Wert: 1" oder "| Mo | 12 |" werden verstanden:
    /// erste Spalte = Name, letzte Spalte = Wert.
    /// </summary>
    public IEnumerable<(string Key, string Value)> Pairs(bool includeSettings = false)
    {
        foreach (var raw in Lines)
        {
            var line = raw.Trim().TrimStart('-', '*', '•').Trim();
            if (line.Contains('|'))
            {
                var cells = line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (cells.Length >= 2 && !cells.All(c => c.All(ch => ch is '-' or ':')))
                {
                    yield return (CellValue(cells[0]), CellValue(cells[^1]));
                    continue;
                }
            }
            var colon = line.LastIndexOf(':');
            if (colon <= 0)
                continue;
            var key = line[..colon].Trim().Trim('*');
            var value = line[(colon + 1)..].Trim();
            if (!includeSettings && SettingKeys.Contains(key))
                continue;
            yield return (key, value);
        }
    }

    /// <summary>"Zeitpunkt: 0" → "0" (der Teil nach dem Doppelpunkt), sonst die Zelle selbst.</summary>
    private static string CellValue(string cell)
    {
        var colon = cell.IndexOf(':');
        return colon >= 0 && colon < cell.Length - 1 ? cell[(colon + 1)..].Trim() : cell.Trim();
    }

    /// <summary>Datenpunkte "Name: Zahl" – Zeilen ohne Zahl werden übersprungen.</summary>
    public List<(string Label, double Value)> Numbers() =>
        Pairs().Select(p => (p.Key, Value: ParseNumber(p.Value))).Where(p => p.Value is not null).Select(p => (p.Key, p.Value!.Value)).ToList();

    /// <summary>"12,5", "12.5", "1.234,5", "70 %", "3 GB" → Zahl.</summary>
    internal static double? ParseNumber(string text)
    {
        var s = new string(text.TakeWhile(c => char.IsDigit(c) || c is '.' or ',' or '-' or ' ' or '+').ToArray()).Replace(" ", "");
        if (s.Length == 0)
            return null;
        if (s.Contains(',') && s.Contains('.'))
            s = s.IndexOf(',') > s.IndexOf('.') ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (s.Contains(','))
            s = s.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public static string FormatNumber(double value) =>
        value.ToString(Math.Abs(value) >= 100 || value == Math.Round(value) ? "#,0" : "#,0.##", German);
}

/// <summary>Farben für Diagramme – passend zu Max' Palette.</summary>
internal static class ChartColors
{
    public static readonly Color[] Palette =
    [
        Theme.Accent, new(255, 150, 40), new(80, 200, 210), new(100, 150, 240),
        new(90, 200, 120), new(210, 110, 210), new(230, 200, 80), new(150, 150, 150),
    ];

    public static Color At(int index) => Palette[index % Palette.Length];

    /// <summary>"rot-gelb", "grün zu blau", "von Rot nach Pink" → zwei Farben; unbekannt → Max-Verlauf.</summary>
    public static (Color From, Color To) Gradient(string? spec) => TryGradient(spec) ?? (Theme.GradientStart, Theme.GradientEnd);

    public static (Color From, Color To)? TryGradient(string? spec)
    {
        var colors = (spec ?? "").Split(['-', ' ', '→', '>', ',', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => ColorTags.TryGet(word.Trim().TrimEnd('.'), out var c) ? c : (Color?)null)
            .OfType<Color>()
            .ToList();
        return colors.Count >= 2 ? (colors[0], colors[1]) : null;
    }
}
