using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Max.Ui.Widgets;

/// <summary><c>```balken</c> – Balkendiagramm aus "Name: Zahl"-Zeilen.</summary>
internal sealed class BarWidget : IWidget
{
    public string Name => "balken";

    public IRenderable? Render(WidgetBody body, int width)
    {
        var data = body.Numbers();
        if (data.Count == 0)
            return null;
        var chart = new BarChart().Width(Math.Min(width, 80));
        if (body.Setting("Titel") is { Length: > 0 } title)
            chart.Label($"[bold {Theme.Tag(Theme.Text)}]{Markup.Escape(title)}[/]").LeftAlignLabel();
        var gradient = ChartColors.TryGradient(body.Setting("Verlauf") ?? body.Setting("Farbe"));
        for (var i = 0; i < data.Count; i++)
        {
            var color = gradient is { } g ? Theme.Blend(g.From, g.To, data.Count == 1 ? 0 : (float)i / (data.Count - 1)) : ChartColors.At(i);
            chart.AddItem(Markup.Escape(data[i].Label), Math.Round(data[i].Value, 2), color);
        }
        return chart;
    }
}

/// <summary><c>```anteile</c> – ein Balken, aufgeteilt in farbige Anteile, mit Legende.</summary>
internal sealed class BreakdownWidget : IWidget
{
    public string Name => "anteile";

    public IRenderable? Render(WidgetBody body, int width)
    {
        var data = body.Numbers().Where(d => d.Value > 0).ToList();
        if (data.Count == 0)
            return null;
        // Spectre zeigt die Werte als Prozent – also erst auf Anteile umrechnen, den echten Wert in die Legende.
        var sum = data.Sum(d => d.Value);
        var chart = new BreakdownChart().Width(Math.Min(width, 80)).ShowPercentage();
        for (var i = 0; i < data.Count; i++)
            chart.AddItem(
                $"{data[i].Label} ({WidgetBody.FormatNumber(data[i].Value)})",
                Math.Round(data[i].Value / sum * 100, 1),
                ChartColors.At(i));
        return WithTitle(body, chart);
    }

    internal static IRenderable WithTitle(WidgetBody body, IRenderable content) =>
        body.Setting("Titel") is { Length: > 0 } title
            ? new Rows(new Markup($"[bold {Theme.Tag(Theme.Text)}]{Markup.Escape(title)}[/]"), content)
            : content;
}

/// <summary><c>```fortschritt</c> – Fortschrittsbalken wie beim Download, "Name: Prozent".</summary>
internal sealed class ProgressWidget : IWidget
{
    public string Name => "fortschritt";

    public IRenderable? Render(WidgetBody body, int width)
    {
        var data = body.Numbers();
        if (data.Count == 0)
            return null;
        var labelWidth = Math.Min(data.Max(d => d.Label.Length), 24);
        var barWidth = Math.Clamp(width - labelWidth - 10, 10, 40);
        var rows = data.Select(d =>
        {
            var fraction = Math.Clamp(d.Value / 100, 0, 1);
            var filled = (int)Math.Round(fraction * barWidth);
            var color = fraction >= 1 ? Theme.Success : Theme.Accent;
            var label = d.Label.Length > labelWidth ? d.Label[..(labelWidth - 1)] + "…" : d.Label.PadRight(labelWidth);
            return (IRenderable)new Markup(
                $"[{Theme.Tag(Theme.Text)}]{Markup.Escape(label)}[/]  [{Theme.Tag(color)}]{new string('━', filled)}[/][{Theme.Tag(Theme.Track)}]{new string('━', barWidth - filled)}[/]  [{Theme.Tag(Theme.Muted)}]{(int)Math.Round(fraction * 100),3} %[/]");
        });
        return BreakdownWidget.WithTitle(body, new Rows(rows));
    }
}

/// <summary>
/// <c>```kurve</c> – Liniendiagramm aus Braille-Zeichen: Jedes Zeichen hat 2×4 Punkte,
/// so wird die Linie viel feiner, als es mit ganzen Zeichen ginge.
/// </summary>
internal sealed class LineChartWidget : IWidget
{
    private const int Height = 8; // Zeichenzeilen → 32 Punkte hoch

    public string Name => "kurve";

    public IRenderable? Render(WidgetBody body, int width)
    {
        var data = body.Numbers();
        if (data.Count < 2)
            return null;

        var min = data.Min(d => d.Value);
        var max = data.Max(d => d.Value);
        if (max - min < 1e-9)
            max = min + 1;

        var minLabel = WidgetBody.FormatNumber(min);
        var maxLabel = WidgetBody.FormatNumber(max);
        var axisWidth = Math.Max(minLabel.Length, maxLabel.Length);
        var plotWidth = Math.Clamp(width - axisWidth - 3, 10, 70);

        var canvas = new BrailleCanvas(plotWidth, Height);
        (int X, int Y) Point(int i) => (
            (int)Math.Round(i * (canvas.DotWidth - 1) / (double)(data.Count - 1)),
            (int)Math.Round((max - data[i].Value) / (max - min) * (canvas.DotHeight - 1)));
        for (var i = 1; i < data.Count; i++)
            canvas.Line(Point(i - 1), Point(i));

        var lines = canvas.Render();
        var color = ChartColors.Gradient(body.Setting("Verlauf") ?? body.Setting("Farbe"));
        var border = Theme.Tag(Theme.Border);
        var muted = Theme.Tag(Theme.Muted);
        var rows = new List<IRenderable>();
        for (var r = 0; r < lines.Length; r++)
        {
            var axis = r == 0 ? maxLabel : r == lines.Length - 1 ? minLabel : "";
            var lineColor = Theme.Blend(color.From, color.To, (float)r / (lines.Length - 1));
            rows.Add(new Markup($"[{muted}]{axis.PadLeft(axisWidth)}[/] [{border}]┤[/][{Theme.Tag(lineColor)}]{lines[r]}[/]"));
        }

        var first = data[0].Label;
        var last = data[^1].Label;
        var gap = Math.Max(1, plotWidth - first.Length - last.Length);
        rows.Add(new Markup($"{new string(' ', axisWidth + 1)}[{border}]└{new string('─', plotWidth)}[/]"));
        rows.Add(new Markup($"{new string(' ', axisWidth + 2)}[{muted}]{Markup.Escape(first)}{new string(' ', gap)}{Markup.Escape(last)}[/]"));
        return BreakdownWidget.WithTitle(body, new Rows(rows));
    }
}

/// <summary>Zeichenfläche aus Braille-Zeichen (U+2800): 2 Punkte breit, 4 hoch pro Zeichen.</summary>
internal sealed class BrailleCanvas(int columns, int rows)
{
    private readonly bool[,] _dots = new bool[columns * 2, rows * 4];

    public int DotWidth => columns * 2;
    public int DotHeight => rows * 4;

    public void Set(int x, int y)
    {
        if (x >= 0 && y >= 0 && x < DotWidth && y < DotHeight)
            _dots[x, y] = true;
    }

    /// <summary>Linie zwischen zwei Punkten (Bresenham).</summary>
    public void Line((int X, int Y) a, (int X, int Y) b)
    {
        int x = a.X, y = a.Y, dx = Math.Abs(b.X - a.X), dy = -Math.Abs(b.Y - a.Y);
        int sx = a.X < b.X ? 1 : -1, sy = a.Y < b.Y ? 1 : -1, error = dx + dy;
        while (true)
        {
            Set(x, y);
            if (x == b.X && y == b.Y)
                break;
            var e2 = 2 * error;
            if (e2 >= dy) { error += dy; x += sx; }
            if (e2 <= dx) { error += dx; y += sy; }
        }
    }

    // Bit für jeden Punkt im Braille-Zeichen: [x, y]
    private static readonly int[,] Bits = { { 0x01, 0x02, 0x04, 0x40 }, { 0x08, 0x10, 0x20, 0x80 } };

    public string[] Render()
    {
        var lines = new string[rows];
        for (var r = 0; r < rows; r++)
        {
            var line = new StringBuilder(columns);
            for (var c = 0; c < columns; c++)
            {
                var code = 0;
                for (var dx = 0; dx < 2; dx++)
                    for (var dy = 0; dy < 4; dy++)
                        if (_dots[c * 2 + dx, r * 4 + dy])
                            code |= Bits[dx, dy];
                line.Append((char)(0x2800 + code));
            }
            lines[r] = line.ToString();
        }
        return lines;
    }
}
