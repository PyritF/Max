using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Max.Ui.Widgets;

/// <summary>
/// <c>```titel</c> – großer Schriftzug (FIGlet), wahlweise mit Farbverlauf wie beim Max-Logo.
/// Schriften: eingebaut (small, slant, big, banner, block, shadow, smslant, mini, script, standard)
/// oder eigene <c>.flf</c>-Dateien im Ordner <c>fonts/</c> des Datenordners.
/// </summary>
internal sealed class TitleWidget : IWidget
{
    /// <summary>Ordner für eigene Schriften; wird beim Start gesetzt.</summary>
    public static string? UserFontDirectory { get; set; }

    public string Name => "titel";

    public static IEnumerable<string> BuiltInFonts =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith("font.", StringComparison.Ordinal) && n.EndsWith(".flf", StringComparison.Ordinal))
            .Select(n => n[5..^4])
            .Append("standard")
            .Order();

    public IRenderable? Render(WidgetBody body, int width)
    {
        // Modelle schreiben gern "Titel:" statt "Text:" – beides zählt.
        var text = body.Setting("Text") ?? body.Setting("Titel") ?? body.Lines.FirstOrDefault(l => !l.Contains(':'))?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var font = LoadFont(body.Setting("Schrift")) ?? FigletFont.Default;
        var lines = RenderPlain(new FigletText(font, text), width);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0)
            return null;

        var single = body.Setting("Farbe") is { } name && ColorTags.TryGet(name, out var c) ? c : (Color?)null;
        var (from, to) = ChartColors.Gradient(body.Setting("Verlauf"));
        var rows = lines.Select((line, i) =>
        {
            var color = single ?? Theme.Blend(from, to, lines.Count == 1 ? 0 : (float)i / (lines.Count - 1));
            return (IRenderable)new Markup($"[bold {Theme.Tag(color)}]{Markup.Escape(line)}[/]");
        });
        return new Rows(rows);
    }

    internal static FigletFont? LoadFont(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var key = name.Trim().ToLowerInvariant().Replace(' ', '-');
        try
        {
            if (UserFontDirectory is { } dir)
            {
                var file = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, "*.flf").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(key, StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(f).Replace(' ', '-').Equals(key, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (file is not null)
                    return FigletFont.Load(file);
            }

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"font.{key}.flf");
            return stream is null ? null : FigletFont.Load(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Zeichnet den Schriftzug ohne Farben in Textzeilen – die Farben kommen danach zeilenweise dazu.</summary>
    private static List<string> RenderPlain(IRenderable renderable, int width)
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = width;
        console.Write(renderable);
        return writer.ToString().ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).ToList();
    }
}
