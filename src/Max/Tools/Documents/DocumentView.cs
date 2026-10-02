using System.Text;
using System.Text.RegularExpressions;

namespace Max.Tools.Documents;

/// <summary>
/// Zeigt aus einem Dokument, was ins Werkzeug-Ergebnis passt: ohne Angabe den Anfang, mit "Seite 7" (Folie, Blatt,
/// Teil, Zeile) diese Stelle, sonst die Stellen zu den Suchbegriffen. Die erste Zeile sagt, was gezeigt wird, die
/// letzte, wie das Modell an den Rest kommt – so findet es auch in 300 Seiten die eine Stelle.
/// Passt das ganze Dokument hinein, gibt es einfach alles.
/// </summary>
internal static partial class DocumentView
{
    /// <summary>So viele Zeichen höchstens – darunter kürzt die Toolbox nichts weg.</summary>
    internal const int Budget = 7000;

    /// <summary>Eine einzelne verlangte Seite bekommt Nachbarn dazu, bis so viel zusammenkommt.</summary>
    internal const int SingleBudget = 3500;

    /// <summary>Eingescannte Seiten abzulesen dauert – mehr als so viele je Aufruf nicht.</summary>
    internal const int MaxScansPerCall = 2;

    /// <param name="title">"Datei C:\…\vertrag.pdf (1,2 MB)" bzw. "Webseite https://…".</param>
    /// <param name="call">Der Aufruf ohne Angabe ("datei: vertrag.pdf") – für die Hinweise, wie es weitergeht.</param>
    /// <param name="selector">Was hinter " | " stand: leer, "Seite 7", "Zeile 120", "Blatt Umsatz" oder Suchbegriffe.</param>
    public static Task<string> RenderAsync(Document document, string title, string call, string selector, CancellationToken ct) =>
        new View(document, $"{title} – {Info(document)}", call, ct).RenderAsync(Parse(selector));

    /// <summary>Was hinter " | " steht.</summary>
    internal abstract record Selection
    {
        internal sealed record Start : Selection;
        internal sealed record End : Selection;
        internal sealed record Parts(int From, int? To) : Selection;
        internal sealed record Lines(int From, int? To, string? Sheet = null) : Selection;
        internal sealed record Search(string Query, IReadOnlyList<string> Terms) : Selection;
    }

    internal static Selection Parse(string selector)
    {
        var text = selector.Trim().Trim('"', '„', '“', '”', '\'', '`').Trim().TrimEnd('.');
        if (text.Length == 0 || StartRegex().IsMatch(text))
            return new Selection.Start();
        if (EndRegex().IsMatch(text))
            return new Selection.End();
        if (PartRegex().Match(text) is { Success: true } part)
            return new Selection.Parts(int.Parse(part.Groups["from"].Value), Number(part.Groups["to"]));
        if (LineRegex().Match(text) is { Success: true } line)
            return new Selection.Lines(int.Parse(line.Groups["from"].Value), Number(line.Groups["to"]));
        if (SheetRegex().Match(text) is { Success: true } sheet)
            return new Selection.Lines(Number(sheet.Groups["from"]) ?? 0, Number(sheet.Groups["to"]), sheet.Groups["name"].Value.Trim());
        var terms = DocumentSearch.Terms(text);
        return terms.Count == 0 ? new Selection.Start() : new Selection.Search(text, terms);

        static int? Number(Group group) => group.Success ? int.Parse(group.Value) : null;
    }

    /// <summary>"PDF, 23 Seiten" (mit Hinweis, falls nur ein Teil gelesen wurde).</summary>
    internal static string Info(Document document) =>
        $"{document.Kind}, {Document.Count(document.Total, document.Unit)}{(document.Note is { } note ? $" ({note})" : "")}";

    private sealed class View(Document document, string head, string call, CancellationToken ct)
    {
        private int _scans = MaxScansPerCall;
        private readonly StringBuilder _out = new();

        private bool Multi => !document.ByLines;

        public async Task<string> RenderAsync(Selection selection)
        {
            if (!document.Parts.Any(p => p.Scanned) && document.Parts.Sum(p => p.Length + Label(p).Length) <= Budget - head.Length)
                return head + ":\n" + string.Concat(document.Parts.Select(p => Label(p) + p.Text + "\n")).TrimEnd();

            return selection switch
            {
                Selection.Search search => await SearchAsync(search),
                Selection.End => await PartsAsync(Tail(), "Ende"),
                Selection.Parts { From: var from, To: var to } => await RangeAsync(from, to),
                Selection.Lines lines => Lines(lines),
                _ => await PartsAsync(document.Parts, null),
            };
        }

        // ── Seiten, Folien, Blätter ──

        private async Task<string> RangeAsync(int from, int? to)
        {
            if (document.ByLines)
                return $"{head}. Eine Textdatei hat keine {Document.Plural("Seite")} – nimm `{call} | Zeile {Math.Max(1, from)}` oder einen Suchbegriff (`{call} | Suchbegriff`).";
            var parts = document.Parts.Where(p => p.Number >= from && (to is null || p.Number <= to)).ToList();
            if (parts.Count == 0)
            {
                return from > document.Total || document.Parts.Count == 0
                    ? $"{head}. Es gibt nur {Document.Count(document.Total, document.Unit)}."
                    : $"{head}. {document.Unit} {from} habe ich nicht gelesen ({document.Note}).";
            }
            // Eine einzelne Seite: dazu die nächsten, solange es knapp bleibt – oft geht der Gedanke dort weiter.
            if (to is null)
            {
                var single = new List<DocumentPart> { parts[0] };
                var size = parts[0].Length;
                foreach (var next in parts.Skip(1))
                {
                    if (next.Scanned || size + next.Length > SingleBudget)
                        break;
                    single.Add(next);
                    size += next.Length;
                }
                parts = single;
            }
            return await PartsAsync(parts, null);
        }

        private IReadOnlyList<DocumentPart> Tail()
        {
            var tail = new List<DocumentPart>();
            var size = 0;
            foreach (var part in document.Parts.Reverse())
            {
                if (tail.Count > 0 && size + part.Length > Budget - head.Length - 200)
                    break;
                tail.Insert(0, part);
                size += part.Length;
            }
            return tail;
        }

        /// <summary>Die Stücke der Reihe nach, bis das Budget voll ist.</summary>
        /// <param name="what">So heißt das Gezeigte, falls nicht "Seiten 3–5".</param>
        /// <param name="note">Steht vor "Gezeigt:", z. B. dass die Suche nichts fand.</param>
        private async Task<string> PartsAsync(IReadOnlyList<DocumentPart> parts, string? what, string note = "")
        {
            if (document.ByLines)
                return Lines(new Selection.Lines(1, null), note);

            var shown = new List<DocumentPart>();
            int? cutAt = null;
            foreach (var part in parts)
            {
                await ReadScanAsync(part);
                var block = Label(part) + (part.Scanned ? ScanHint(part) : part.Text) + "\n";
                if (_out.Length + block.Length <= Budget - head.Length - 200)
                {
                    _out.Append(block);
                    shown.Add(part);
                    continue;
                }
                if (shown.Count == 0)
                {
                    // Schon das erste Stück ist zu lang: so viele Zeilen, wie passen.
                    _out.Append(Label(part));
                    var next = AppendLines(part, 0, null);
                    cutAt = next < part.Lines.Count ? next : null;
                    shown.Add(part);
                }
                break;
            }

            var first = shown[0];
            var last = shown[^1];
            var range = what ?? (first == last ? $"{document.Unit} {first.Number}" : $"{Document.Plural(document.Unit)} {first.Number}–{last.Number}");
            var text = new StringBuilder($"{head}. {note}Gezeigt: {(cutAt is null ? range : range + " (gekürzt)")}.\n").Append(_out);
            if (cutAt is { } line && last.LineNumbers is { } numbers && last.Name is { } sheet)
                text.Append($"(Weiter mit `{call} | Blatt {sheet}, Zeile {numbers[line]}` – oder gezielt suchen: `{call} | Suchbegriff`.)");
            else if (cutAt is not null)
                text.Append($"({last.Label} ist lang und hier gekürzt – den Rest findest du gezielt: `{call} | Suchbegriff`.)");
            else if (document.Parts.FirstOrDefault(p => p.Number > last.Number) is { } next)
                text.Append($"(Weiter mit `{call} | {document.Unit} {next.Number}` – oder gezielt suchen: `{call} | Suchbegriff`.)");
            return text.ToString().TrimEnd();
        }

        // ── Zeilen (Textdateien, Tabellenblätter) ──

        private string Lines(Selection.Lines selection, string note = "")
        {
            DocumentPart? part;
            if (selection.Sheet is { } name)
            {
                var folded = DocumentSearch.Fold(name);
                part = document.Parts.FirstOrDefault(p => p.Name is { } n && DocumentSearch.Fold(n) == folded)
                    ?? document.Parts.FirstOrDefault(p => p.Name is { } n && DocumentSearch.Fold(n).Contains(folded, StringComparison.Ordinal));
                if (part is null)
                {
                    var sheets = string.Join(", ", document.Parts.Where(p => p.Name is not null).Select(p => $"„{p.Name}“"));
                    return sheets.Length > 0 ? $"{head}. Ein Blatt „{name}“ gibt es nicht – nur {sheets}." : $"{head}. Hier gibt es keine Tabellenblätter.";
                }
            }
            else
            {
                part = document.Parts.FirstOrDefault(p => p.LineNumbers is not null);
                if (part is null)
                    return $"{head}. Hier gibt es keine Zeilennummern – nimm `{call} | {document.Unit} 1` oder einen Suchbegriff (`{call} | Suchbegriff`).";
            }

            var numbers = part.LineNumbers!;
            var start = 0;
            while (start < numbers.Count && numbers[start] < selection.From)
                start++;
            if (start >= numbers.Count)
                return $"{head}. So weit geht es nicht – die letzte Zeile ist {(numbers.Count > 0 ? numbers[^1] : 0)}.";

            if (Multi)
                _out.Append(Label(part));
            var next = AppendLines(part, start, selection.To);
            var shownTo = numbers[Math.Max(start, next - 1)];
            var where = Multi ? $"{part.Label}, Zeile {numbers[start]}–{shownTo}" : $"Zeile {numbers[start]}–{shownTo}";
            var text = new StringBuilder($"{head}. {note}Gezeigt: {where}.\n").Append(_out);
            if (next < numbers.Count && (selection.To is null || numbers[next] <= selection.To))
            {
                var more = part.Name is { } sheet ? $"Blatt {sheet}, Zeile {numbers[next]}" : $"Zeile {numbers[next]}";
                text.Append($"(Weiter mit `{call} | {more}` – oder gezielt suchen: `{call} | Suchbegriff`.)");
            }
            else if (Multi && selection.Sheet is null && document.Parts.Count > 1)
            {
                text.Append($"(Das war {part.Label}. Andere Blätter: `{call} | Blatt <Name>`.)");
            }
            return text.ToString().TrimEnd();
        }

        /// <summary>Zeilen ab <paramref name="start"/> (bis Zeile <paramref name="to"/>), solange das Budget reicht. Gibt die erste nicht gezeigte zurück.</summary>
        private int AppendLines(DocumentPart part, int start, int? to)
        {
            var limit = Budget - head.Length - 250;
            var i = start;
            for (; i < part.Lines.Count; i++)
            {
                if (to is { } last && part.LineNumbers is { } numbers && numbers[i] > last)
                    break;
                var line = part.Lines[i];
                if (_out.Length + line.Length + 1 > limit)
                {
                    if (i == start)
                    {
                        // Eine einzige Riesenzeile: wenigstens ihr Anfang.
                        _out.Append(line[..Math.Max(0, Math.Min(line.Length, limit - _out.Length - 1))]).Append(" …\n");
                        i++;
                    }
                    break;
                }
                _out.Append(line).Append('\n');
            }
            return i;
        }

        // ── Suchen ──

        private async Task<string> SearchAsync(Selection.Search search)
        {
            var chunks = DocumentSearch.Chunks(document);
            var ranked = DocumentSearch.Rank(chunks, search.Terms);
            if (ranked.Count == 0)
            {
                // Eingescannte Seiten sind erst nach dem Ablesen durchsuchbar – dann eben der Anfang, abgelesen.
                if (document.Parts.Any(p => p.Scanned))
                    return await PartsAsync(document.Parts, null) + ScanNote();
                return await PartsAsync(document.Parts, null, $"Zu „{search.Query}“ steht darin nichts – hier der Anfang. ");
            }

            var limit = Budget - head.Length - 400;
            var chosen = new List<DocumentSearch.Chunk>();
            var size = 0;
            foreach (var (chunk, _) in ranked)
            {
                if (chosen.Count >= 10 || size + chunk.Text.Length > limit)
                    continue;
                chosen.Add(chunk);
                size += chunk.Text.Length + 40;
            }
            if (chosen.Count == 0)
                chosen.Add(ranked[0].Chunk with { Text = ranked[0].Chunk.Text[..Math.Min(ranked[0].Chunk.Text.Length, limit)] });

            // Der Anfang zur Orientierung (Titel, Parteien eines Vertrags …), falls er nicht schon dabei ist – nicht bei Code.
            var opening = Multi && chunks.Count > 0 && !chosen.Contains(chunks[0]) && size + 600 <= Budget - head.Length - 200 ? chunks[0] : null;
            var text = new StringBuilder($"{head}. Stellen zu „{search.Query}“:\n");
            if (opening is not null)
                text.Append($"--- Anfang ({opening.Part.Label}) ---\n").Append(Shorten(opening.Text, 500)).Append('\n');

            DocumentPart? previousPart = null;
            var previousTo = -1;
            foreach (var chunk in chosen.OrderBy(c => c.Part.Number).ThenBy(c => c.From))
            {
                var numbers = chunk.Part.LineNumbers;
                if (numbers is not null)
                {
                    var where = $"Zeile {numbers[chunk.From]}–{numbers[Math.Max(chunk.From, chunk.To - 1)]}";
                    text.Append("--- ").Append(Multi ? $"{chunk.Part.Label}, {where}" : where).Append(" ---\n");
                    // In Tabellen erklärt die Kopfzeile die Spalten.
                    if (chunk.Part.Name is not null && chunk.From > 0 && chunk.Part.Lines.Count > 0)
                        text.Append("(Kopfzeile: ").Append(chunk.Part.Lines[0]).Append(")\n");
                }
                else if (chunk.Part != previousPart)
                {
                    text.Append("--- ").Append(chunk.Part.Label).Append(" ---\n");
                }
                else if (chunk.From > previousTo)
                {
                    text.Append("…\n");
                }
                text.Append(chunk.Text).Append('\n');
                previousPart = chunk.Part;
                previousTo = chunk.To;
            }

            var best = ranked[0].Chunk;
            text.Append(best.Part.LineNumbers is { } lines
                ? $"(Mehr Zusammenhang: `{call} | {(best.Part.Name is { } sheet ? $"Blatt {sheet}, " : "")}Zeile {lines[Math.Max(0, best.From - 10)]}`.)"
                : $"(Ganz lesen: `{call} | {best.Part.Label}`.)");
            return text.Append(ScanNote()).ToString().TrimEnd();
        }

        /// <summary>Welche Seiten eingescannt und noch nicht abgelesen sind – die findet keine Suche.</summary>
        private string ScanNote()
        {
            var unread = document.Parts.Where(p => p.Scanned).Select(p => p.Number).ToList();
            if (unread.Count == 0 || document.ReadScan is null || _noVision)
                return "";
            var which = string.Join(", ", unread.Take(8)) + (unread.Count > 8 ? " …" : "");
            return $"\n(Eingescannt und noch nicht durchsucht: {document.Unit} {which} – einzeln lesen mit `{call} | {document.Unit} {unread[0]}`.)";
        }

        // ── Eingescannte Seiten ──

        private async Task ReadScanAsync(DocumentPart part)
        {
            if (!part.Scanned || document.ReadScan is null || _noVision || _scans <= 0)
                return;
            var text = await document.ReadScan(part, ct);
            if (text is null)
            {
                _noVision = true;           // Bildverständnis noch nicht da – die Seite bleibt ungelesen
                return;
            }
            _scans--;
            part.SetScannedText(text.Length == 0
                ? "(eingescannt – das Bild darauf lässt sich nicht lesen)"
                : "(eingescannt, vom Bild abgelesen)\n" + text);
        }

        private bool _noVision;

        private string ScanHint(DocumentPart part) => document.ReadScan is null || _noVision
            ? "(eingescannt – nur ein Bild. Das Bildverständnis ist noch nicht auf diesem Rechner, deshalb kann ich die Seite nicht lesen.)"
            : $"(eingescannt – lesen mit `{call} | {document.Unit} {part.Number}`)";

        private string Label(DocumentPart part) => Multi ? $"--- {part.Label} ---\n" : "";
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + " …";

    [GeneratedRegex(@"^(anfang|beginn|start|erste seite|von vorn)$", RegexOptions.IgnoreCase)]
    private static partial Regex StartRegex();

    [GeneratedRegex(@"^(ende|schluss|am ende|letzte (seite|folie|seiten))$", RegexOptions.IgnoreCase)]
    private static partial Regex EndRegex();

    [GeneratedRegex(@"^(?:ab\s+|auf\s+)?(?:seiten?|s\.|folien?|teile?|abschnitte?|kapitel|bl[aä]tt(?:er)?)\s*(?<from>\d{1,5})(?:\s*(?:-|–|bis)\s*(?<to>\d{1,5}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex PartRegex();

    [GeneratedRegex(@"^(?:ab\s+)?(?:zeilen?|z\.)\s*(?<from>\d{1,7})(?:\s*(?:-|–|bis)\s*(?<to>\d{1,7}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"^(?:tabellen)?bl[aä]tt\s+[„""']?(?<name>[^,„""“”']+?)[“”""']?(?:\s*,\s*(?:ab\s+)?zeilen?\s+(?<from>\d{1,7})(?:\s*(?:-|–|bis)\s*(?<to>\d{1,7}))?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SheetRegex();
}
