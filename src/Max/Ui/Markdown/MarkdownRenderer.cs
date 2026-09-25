using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Max.Ui.Markdown;

/// <summary>
/// Zeigt Max' Antwort als formatiertes Markdown an – schon während sie entsteht.
/// Gearbeitet wird Zeile für Zeile: Der Anfang einer Zeile wird kurz zurückgehalten, bis klar ist,
/// was sie ist (Überschrift, Liste, Zitat, normaler Text …); danach fließt der Text Wort für Wort weiter.
/// Code-Zeilen und Tabellen brauchen die ganze Zeile bzw. die ganze Tabelle und erscheinen, sobald sie fertig sind.
/// Ein Block in <c>```markdown</c> wird nicht als Code gezeigt, sondern selbst formatiert.
/// </summary>
internal sealed partial class MarkdownRenderer
{
    private enum Mode { LineStart, Inline, WholeLine }

    private static readonly Style Normal = new(Theme.Text);
    private static readonly Style Border = new(Theme.Border);
    private static readonly Style Marker = new(Theme.Accent);
    private static readonly Style Label = new(Theme.Muted);
    private static readonly Style Code = new(new Color(200, 200, 200));

    private readonly IAnsiConsole _console;
    private readonly WrapWriter _writer;
    private readonly InlineFormatter _inline;
    private readonly StringBuilder _line = new();
    private readonly List<string> _tableRows = [];
    private Mode _mode = Mode.LineStart;
    private bool _inCode;
    private bool _inMarkdownFence;

    public MarkdownRenderer(IAnsiConsole console, WrapWriter writer)
    {
        _console = console;
        _writer = writer;
        _inline = new InlineFormatter(writer.Write);
    }

    public void Push(string chunk)
    {
        var inlineText = new StringBuilder();

        foreach (var c in chunk)
        {
            if (c == '\r')
                continue;

            if (_mode == Mode.Inline)
            {
                if (c == '\n')
                {
                    FlushInline(inlineText);
                    _inline.EndLine();
                    _writer.EndLine();
                    _mode = Mode.LineStart;
                }
                else
                {
                    inlineText.Append(c);
                }
                continue;
            }

            if (c == '\n')
            {
                CompleteLine(_line.ToString());
                _line.Clear();
                _mode = Mode.LineStart;
                continue;
            }

            _line.Append(c);
            if (_mode == Mode.LineStart && !_inCode)
                TryDecideLineStart();
        }

        FlushInline(inlineText);
    }

    /// <summary>Am Ende (oder bei Abbruch): Angefangenes ausgeben, offene Blöcke schließen.</summary>
    public void Finish()
    {
        if (_mode == Mode.Inline)
            _inline.EndLine();
        else if (_line.Length > 0)
            CompleteLine(_line.ToString());
        _line.Clear();

        if (_inCode)
            CloseCodeBlock();
        FlushTable();
        _writer.Finish();
    }

    private void FlushInline(StringBuilder text)
    {
        if (text.Length == 0)
            return;
        _inline.Push(text.ToString());
        text.Clear();
    }

    // ── Zeilenanfang ──

    /// <summary>Steht fest, was für eine Zeile das wird? Dann Präfix zeichnen und in den Fließtext wechseln.</summary>
    private void TryDecideLineStart()
    {
        var line = _line.ToString();
        var content = line.TrimStart(' ');
        if (content.Length == 0)
            return;

        // Code-Zaun und Tabellenzeile werden als Ganzes gebraucht.
        if (content.StartsWith("```", StringComparison.Ordinal) || content.StartsWith('|'))
        {
            _mode = Mode.WholeLine;
            return;
        }

        // Noch offen: "`" / "``" (Code-Zaun oder Inline-Code?) und "---" (Trennlinie oder Liste?).
        if (content.All(ch => ch == '`') || IsRuleCandidate(content))
            return;

        if (Classify(line, complete: false) is { } block)
            StartBlock(block, line);
    }

    /// <summary>Nur "-", "*" oder "_" ohne Leerzeichen – könnte noch eine Trennlinie werden.</summary>
    private static bool IsRuleCandidate(string content) =>
        content[0] is '-' or '*' or '_' && content.All(ch => ch == content[0]);

    private sealed record Block(IReadOnlyList<(string, Style)> Prefix, IReadOnlyList<(string, Style)> Continuation, Style Style, int ContentStart);

    /// <summary>Überschrift, Liste, Zitat oder Text? null = noch nicht entscheidbar.</summary>
    private static Block? Classify(string line, bool complete)
    {
        var spaces = line.Length - line.TrimStart(' ').Length;
        var content = line[spaces..];
        var nest = new string(' ', Math.Min(spaces / 2, 4) * 2);

        if (content.StartsWith('#'))
        {
            var level = content.TakeWhile(ch => ch == '#').Count();
            if (level < content.Length && content[level] == ' ' && level <= 6)
                return new Block([], [], HeadingStyle(level), spaces + level + 1);
            if (!complete && level == content.Length)
                return null;
        }

        if (content[0] is '-' or '*' or '+')
        {
            if (content.Length >= 2 && content[1] == ' ')
                return new Block([(nest + "• ", Marker)], [(nest + "  ", Normal)], Normal, spaces + 2);
            if (!complete && content.Length == 1)
                return null;
        }

        if (char.IsAsciiDigit(content[0]))
        {
            var match = OrderedRegex().Match(content);
            if (match.Success)
            {
                var marker = match.Groups[1].Value + ". ";
                return new Block([(nest + marker, Marker)], [(nest + new string(' ', marker.Length), Normal)], Normal, spaces + match.Length);
            }
            if (!complete && content.Length <= 4 && content.All(ch => char.IsAsciiDigit(ch) || ch is '.' or ')'))
                return null;
        }

        if (content[0] == '>')
        {
            var start = spaces + (content.Length > 1 && content[1] == ' ' ? 2 : 1);
            if (content.Length > 1 || complete)
                return new Block([("│ ", Label)], [("│ ", Label)], new Style(Theme.Dim, decoration: Decoration.Italic), start);
            return null;
        }

        return new Block([], [], Normal, spaces);
    }

    private void StartBlock(Block block, string line)
    {
        FlushTable();
        _writer.StartLine(block.Prefix, block.Continuation);
        _inline.StartLine(block.Style);
        _mode = Mode.Inline;
        _line.Clear();
        if (block.ContentStart < line.Length)
            _inline.Push(line[block.ContentStart..]);
    }

    private static Style HeadingStyle(int level) => level switch
    {
        1 => new Style(Theme.Accent, decoration: Decoration.Bold),
        2 => new Style(Theme.Text, decoration: Decoration.Bold),
        _ => new Style(Theme.Dim, decoration: Decoration.Bold),
    };

    // ── Ganze Zeilen ──

    private void CompleteLine(string line)
    {
        var trimmed = line.Trim();

        if (_inCode)
        {
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                CloseCodeBlock();
            else
                WriteCodeLine(line);
            return;
        }

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            FlushTable();
            var language = trimmed[3..].Trim().ToLowerInvariant();
            if (_inMarkdownFence && language.Length == 0)
                _inMarkdownFence = false;               // Ende eines ```markdown-Blocks – einfach weglassen
            else if (language is "markdown" or "md")
                _inMarkdownFence = true;                // Markdown im Code-Block: selbst formatieren statt roh zeigen
            else
                OpenCodeBlock(language);
            return;
        }

        if (trimmed.StartsWith('|'))
        {
            _tableRows.Add(trimmed);
            return;
        }

        FlushTable();

        if (trimmed.Length == 0)
        {
            _writer.BlankLine();
            return;
        }

        if (RuleRegex().IsMatch(trimmed))
        {
            _writer.StartLine([(new string('─', Math.Max(3, _writer.LineWidth)), Border)], []);
            _writer.EndLine();
            return;
        }

        // Normale Zeile, die komplett auf einmal ankam (oder erst am Ende entscheidbar war).
        var block = Classify(line, complete: true)!;
        StartBlock(block, line);
        _inline.EndLine();
        _writer.EndLine();
        _mode = Mode.LineStart;
    }

    // ── Code ──

    private void OpenCodeBlock(string language)
    {
        _inCode = true;
        var label = language.Length > 0 ? " " + language : "";
        _writer.StartLine([("┌", Border), (label, Label)], []);
        _writer.EndLine();
    }

    private void WriteCodeLine(string line)
    {
        _writer.StartLine([("│ ", Border)], [("│ ", Border)]);
        if (line.Length > 0)
            _writer.Write(line.Replace("\t", "    "), Code);
        _writer.EndLine();
    }

    private void CloseCodeBlock()
    {
        _inCode = false;
        _writer.StartLine([("└", Border)], []);
        _writer.EndLine();
    }

    // ── Tabellen ──

    private void FlushTable()
    {
        if (_tableRows.Count == 0)
            return;

        var rows = _tableRows.Select(SplitRow).ToList();
        _tableRows.Clear();

        var hasHeader = rows.Count >= 2 && rows[1].All(cell => SeparatorRegex().IsMatch(cell));
        var header = hasHeader ? rows[0] : null;
        var body = rows.Where((_, i) => !hasHeader || i >= 2).Where(r => !r.All(cell => SeparatorRegex().IsMatch(cell))).ToList();
        var columns = rows.Max(r => r.Count);

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Theme.Border);
        if (header is null)
            table.HideHeaders();
        for (var i = 0; i < columns; i++)
        {
            var title = header is not null && i < header.Count ? header[i] : "";
            table.AddColumn(new TableColumn(InlineFormatter.ToMarkup(title, new Style(Theme.Accent, decoration: Decoration.Bold))));
        }
        foreach (var row in body)
            table.AddRow(Enumerable.Range(0, columns).Select(i => new Markup(InlineFormatter.ToMarkup(i < row.Count ? row[i] : "", Normal))).ToArray());

        _writer.BeforeExternalBlock();
        _console.Write(new Padder(table, new Padding(_writer.MaxColumn - _writer.LineWidth, 0, 0, 0)));
        _writer.AfterExternalBlock();
    }

    private static List<string> SplitRow(string row)
    {
        var inner = row.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|')) inner = inner[..^1];
        return inner.Split('|').Select(cell => cell.Trim()).ToList();
    }

    [GeneratedRegex(@"^(\d{1,3})[.)] ")]
    private static partial Regex OrderedRegex();

    [GeneratedRegex(@"^([-*_])( *\1){2,} *$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^:?-{2,}:?$")]
    private static partial Regex SeparatorRegex();
}
