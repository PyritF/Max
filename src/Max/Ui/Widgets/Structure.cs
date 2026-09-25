using System.Globalization;
using Max.Ui.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Max.Ui.Widgets;

/// <summary><c>```baum</c> – eingerückte Liste als Baum (Ordner, Gliederungen, Abhängigkeiten).</summary>
internal sealed class TreeWidget : IWidget
{
    public string Name => "baum";

    public IRenderable? Render(WidgetBody body, int width)
    {
        var items = body.Lines
            .Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("Titel:", StringComparison.OrdinalIgnoreCase))
            .Select(l => (Indent: IndentOf(l), Text: l.Trim().TrimStart(TreeGlyphs).Trim()))
            .Where(i => i.Text.Length > 0)
            .ToList();
        if (items.Count == 0)
            return null;

        var title = body.Setting("Titel");
        var topLevel = items.Min(i => i.Indent);
        var singleRoot = title is null && items.Count(i => i.Indent == topLevel) == 1;
        var rootLabel = singleRoot ? items[0].Text : title ?? "";
        var tree = new Tree(Label(rootLabel, isRoot: true)).Guide(TreeGuide.Line).Style(new Style(Theme.Border));

        // Stapel aus (Einrückung, Knoten) – jede Zeile hängt unter dem letzten weniger eingerückten Eintrag.
        var stack = new List<(int Indent, IHasTreeNodes Node)> { (-1, tree) };
        foreach (var item in singleRoot ? items.Skip(1) : items)
        {
            while (stack.Count > 1 && stack[^1].Indent >= item.Indent)
                stack.RemoveAt(stack.Count - 1);
            var node = stack[^1].Node.AddNode(Label(item.Text, isRoot: false));
            stack.Add((item.Indent, node));
        }
        return tree;
    }

    /// <summary>Zeichen, mit denen Modelle Bäume gern selbst malen ("│   ├── src/") – zählen als Einrückung.</summary>
    private static readonly char[] TreeGlyphs = [' ', '\t', '-', '*', '•', '├', '└', '│', '─', '┣', '┗', '┃', '━', '|', '`', '+'];

    /// <summary>Position des ersten echten Zeichens – so klappt es mit Leerzeichen und mit gemalten Linien.</summary>
    internal static int IndentOf(string line)
    {
        var i = 0;
        while (i < line.Length && TreeGlyphs.Contains(line[i]))
            i++;
        return i;
    }

    private static IRenderable Label(string text, bool isRoot)
    {
        var isFolder = text.EndsWith('/') || text.EndsWith('\\');
        var style = isRoot || isFolder ? new Style(Theme.Accent, decoration: Decoration.Bold) : new Style(Theme.Text);
        return new Markup(InlineFormatter.ToMarkup(text, style));
    }
}

/// <summary><c>```kasten</c> – Text im Rahmen mit Titel, z. B. für Hinweise und Warnungen.</summary>
internal sealed class BoxWidget : IWidget
{
    public string Name => "kasten";

    public IRenderable? Render(WidgetBody body, int width)
    {
        var title = body.Setting("Titel");
        var color = body.Setting("Farbe") is { } name && ColorTags.TryGet(name, out var c) ? c : Theme.Accent;
        var content = body.Lines.Where(l => !IsSetting(l)).ToList();
        while (content.Count > 0 && content[0].Trim().Length == 0)
            content.RemoveAt(0);
        if (content.Count == 0)
            return null;

        var panel = new Panel(new Markup(ToMarkup(content)))
            .Border(BoxBorder.Rounded)
            .BorderColor(color)
            .Padding(1, 0);
        if (title is { Length: > 0 })
            panel.Header($"[bold {Theme.Tag(color)}] {Markup.Escape(title)} [/]");
        return panel;
    }

    internal static bool IsSetting(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("Titel:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Farbe:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Einfaches Markdown im Kasten: fett, kursiv, code, Farben und Aufzählungen.</summary>
    internal static string ToMarkup(IEnumerable<string> lines) => string.Join("\n", lines.Select(line =>
    {
        var trimmed = line.TrimStart();
        var indent = new string(' ', line.Length - trimmed.Length);
        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            return $"{indent}[{Theme.Tag(Theme.Accent)}]•[/] {InlineFormatter.ToMarkup(trimmed[2..], new Style(Theme.Text))}";
        if (trimmed.StartsWith('#'))
            return InlineFormatter.ToMarkup(trimmed.TrimStart('#').Trim(), new Style(Theme.Text, decoration: Decoration.Bold));
        return indent + InlineFormatter.ToMarkup(trimmed, new Style(Theme.Text));
    }));
}

/// <summary><c>```spalten</c> – Abschnitte nebeneinander, getrennt durch eine Zeile "---".</summary>
internal sealed class ColumnsWidget : IWidget
{
    public string Name => "spalten";

    public IRenderable? Render(WidgetBody body, int width)
    {
        var sections = new List<List<string>> { new() };
        foreach (var line in body.Lines)
        {
            if (line.Trim() is "---" or "***")
                sections.Add([]);
            else
                sections[^1].Add(line);
        }
        sections = sections.Where(s => s.Any(l => l.Trim().Length > 0)).ToList();
        if (sections.Count < 2)
            return null;

        // Gleich breite Spalten; der Grid-Abstand zwischen den Spalten beträgt 2.
        var columnWidth = Math.Max(12, (width - 2 * (sections.Count - 1)) / sections.Count);
        var panels = sections.Select(s => (IRenderable)new Panel(new Markup(BoxWidget.ToMarkup(s.SkipWhile(l => l.Trim().Length == 0))))
            .Border(BoxBorder.Rounded).BorderColor(Theme.Border).Padding(1, 0).Expand());
        var grid = new Grid();
        foreach (var _ in sections)
            grid.AddColumn(new GridColumn().Width(columnWidth));
        grid.AddRow(panels.ToArray());
        return grid;
    }
}

/// <summary><c>```kalender</c> – Monatskalender mit markierten Tagen.</summary>
internal sealed class CalendarWidget : IWidget
{
    public string Name => "kalender";

    public IRenderable? Render(WidgetBody body, int width)
    {
        var month = DateTime.Today;
        if (body.Setting("Monat") is { } spec && TryParseMonth(spec, out var parsed))
            month = parsed;

        var calendar = new Spectre.Console.Calendar(month.Year, month.Month)
            .Culture(CultureInfo.GetCultureInfo("de-DE"))
            .RoundedBorder()
            .BorderColor(Theme.Border)
            .HeaderStyle(new Style(Theme.Accent, decoration: Decoration.Bold))
            .HighlightStyle(new Style(Theme.Accent, decoration: Decoration.Bold));

        foreach (var day in (body.Setting("Markiert") ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(day.Trim('.'), out var d) && d >= 1 && d <= DateTime.DaysInMonth(month.Year, month.Month))
                calendar.AddCalendarEvent(month.Year, month.Month, d);
        return calendar;
    }

    /// <summary>"2026-10", "10/2026", "Oktober 2026".</summary>
    internal static bool TryParseMonth(string text, out DateTime month)
    {
        var german = CultureInfo.GetCultureInfo("de-DE");
        foreach (var format in new[] { "yyyy-MM", "MM/yyyy", "M/yyyy", "MMMM yyyy", "MMM yyyy", "MM.yyyy" })
            if (DateTime.TryParseExact(text.Trim(), format, german, DateTimeStyles.None, out month))
                return true;
        month = default;
        return false;
    }
}
