using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Max.Tools.Documents;

/// <summary>
/// Findet in einem Dokument die Stellen zu einer Frage oder ein paar Suchbegriffen: Das Dokument wird in kleine
/// Abschnitte zerlegt, jeder bekommt Punkte nach BM25 (seltene Begriffe zählen mehr, viele Treffer in einem kurzen
/// Abschnitt auch). Deutsch-tauglich: Umlaute egal ("Kuendigung" = "Kündigung"), Wortteile zählen
/// ("Frist" findet "Kündigungsfrist"), einfache Endungen fallen weg ("Rechnungen" findet "Rechnung").
/// </summary>
internal static partial class DocumentSearch
{
    /// <summary>So groß ist ein Abschnitt höchstens (an Zeilengrenzen).</summary>
    internal const int ChunkChars = 700;

    /// <summary>Ein Abschnitt: Zeilen <see cref="From"/> bis vor <see cref="To"/> eines Stücks.</summary>
    internal sealed record Chunk(DocumentPart Part, int From, int To, string Text);

    /// <summary>Die Suchbegriffe einer Frage – ohne Füllwörter, kleingeschrieben, ohne Umlaute und Endungen.</summary>
    public static IReadOnlyList<string> Terms(string query)
    {
        var terms = new List<string>();
        foreach (Match match in WordRegex().Matches(query))
        {
            var word = Fold(match.Value);
            var numeric = word.All(char.IsDigit);
            if (word.Length < 2 || StopWords.Contains(word))
                continue;
            var term = numeric ? word : Stem(word);
            if (!terms.Contains(term))
                terms.Add(term);
        }
        return terms;
    }

    /// <summary>Die besten Abschnitte zu den Begriffen, beste zuerst – leer, wenn nichts passt.</summary>
    public static List<(Chunk Chunk, double Score)> Rank(IReadOnlyList<Chunk> chunks, IReadOnlyList<string> terms)
    {
        if (chunks.Count == 0 || terms.Count == 0)
            return [];
        var folded = chunks.Select(c => Fold(c.Text)).ToList();
        var counts = terms.Select(t => folded.Select(text => Count(text, t)).ToArray()).ToList();
        var average = Math.Max(1, folded.Average(t => t.Length));

        const double k = 1.2, b = 0.5;
        var scored = new List<(Chunk, double)>();
        for (var i = 0; i < chunks.Count; i++)
        {
            double score = 0;
            var matched = 0;
            for (var t = 0; t < terms.Count; t++)
            {
                var tf = counts[t][i];
                if (tf == 0)
                    continue;
                matched++;
                var df = counts[t].Count(c => c > 0);
                var idf = Math.Log(1 + (chunks.Count - df + 0.5) / (df + 0.5));
                score += idf * tf * (k + 1) / (tf + k * (1 - b + b * folded[i].Length / average));
            }
            // Mehrere verschiedene Begriffe in einem Abschnitt sind viel aussagekräftiger als einer oft.
            if (matched > 0)
                scored.Add((chunks[i], score * (1 + 0.5 * (matched - 1))));
        }
        if (scored.Count == 0)
            return [];
        var best = scored.Max(s => s.Item2);
        return scored.Where(s => s.Item2 >= best * 0.25).OrderByDescending(s => s.Item2).ToList();
    }

    /// <summary>Das Dokument in Abschnitte (eingescannte Seiten ohne Text fehlen).</summary>
    public static List<Chunk> Chunks(Document document)
    {
        var chunks = new List<Chunk>();
        foreach (var part in document.Parts.Where(p => !p.Scanned))
        {
            var from = 0;
            var size = 0;
            for (var i = 0; i < part.Lines.Count; i++)
            {
                var line = part.Lines[i];
                if (line.Length > ChunkChars)
                {
                    // Eine sehr lange Zeile (ein ganzer Absatz) wird in mehrere Abschnitte geteilt.
                    Emit(part, from, i);
                    foreach (var piece in DocumentReader.Slice(line, ChunkChars))
                        chunks.Add(new Chunk(part, i, i + 1, piece));
                    from = i + 1;
                    size = 0;
                    continue;
                }
                if (size > 0 && size + line.Length > ChunkChars)
                {
                    Emit(part, from, i);
                    from = i;
                    size = 0;
                }
                size += line.Length + 1;
            }
            Emit(part, from, part.Lines.Count);
        }
        return chunks;

        void Emit(DocumentPart part, int from, int to)
        {
            if (to > from && part.Lines.Skip(from).Take(to - from).Any(l => l.Trim().Length > 0))
                chunks.Add(new Chunk(part, from, to, string.Join('\n', part.Lines.Skip(from).Take(to - from))));
        }
    }

    /// <summary>Wie oft der Begriff vorkommt. Kurze Begriffe und Zahlen nur als ganzes Wort, längere auch in Zusammensetzungen.</summary>
    internal static int Count(string folded, string term)
    {
        var whole = term.Length < 4 || term.All(char.IsDigit);
        var count = 0;
        var index = 0;
        while ((index = folded.IndexOf(term, index, StringComparison.Ordinal)) >= 0)
        {
            if (!whole || (index == 0 || !char.IsLetterOrDigit(folded[index - 1]))
                && (index + term.Length == folded.Length || !char.IsLetterOrDigit(folded[index + term.Length])))
                count++;
            index += term.Length;
        }
        return count;
    }

    /// <summary>Kleinschreibung, Umlaute ausgeschrieben, Akzente weg: "Kündigung" → "kuendigung", "Café" → "cafe".</summary>
    internal static string Fold(string text)
    {
        var lower = text.ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        if (lower.All(c => c < 128))
            return lower;
        var decomposed = lower.Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                plain.Append(c);
        return plain.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly string[] Suffixes = ["en", "er", "es", "e", "n", "s"];

    /// <summary>Einfache Endungen weg, damit "Rechnungen" auch "Rechnung" findet – mindestens 4 Buchstaben bleiben.</summary>
    internal static string Stem(string word)
    {
        foreach (var suffix in Suffixes)
            if (word.Length - suffix.Length >= 4 && word.EndsWith(suffix, StringComparison.Ordinal))
                return word[..^suffix.Length];
        return word;
    }

    /// <summary>Füllwörter (schon ohne Umlaute) – sie finden alles und damit nichts.</summary>
    private static readonly HashSet<string> StopWords =
    [
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einer", "eines", "einem", "einen", "und", "oder", "aber",
        "doch", "denn", "wenn", "dann", "als", "wie", "was", "wer", "wo", "wann", "warum", "wieso", "weshalb", "welche",
        "welcher", "welches", "welchen", "welchem", "ist", "sind", "war", "waren", "wird", "werden", "wurde", "wurden",
        "hat", "haben", "hatte", "hatten", "kann", "koennen", "konnte", "muss", "muessen", "soll", "sollen", "darf",
        "will", "wollen", "moechte", "mag", "ich", "du", "er", "sie", "es", "wir", "ihr", "mir", "mich", "dir", "dich",
        "uns", "euch", "sich", "mein", "meine", "meinen", "meinem", "meiner", "dein", "deine", "sein", "seine", "ihre",
        "ihren", "unser", "unsere", "in", "im", "ins", "an", "am", "auf", "aus", "bei", "beim", "mit", "nach", "von",
        "vom", "vor", "zu", "zum", "zur", "ueber", "unter", "um", "durch", "fuer", "gegen", "ohne", "bis", "seit",
        "nicht", "kein", "keine", "keinen", "noch", "nur", "auch", "schon", "so", "da", "dort", "hier", "dies", "diese",
        "dieser", "dieses", "diesem", "diesen", "jetzt", "bitte", "mal", "steht", "stehen", "stand", "gibt", "geht",
        "genau", "eigentlich", "drin", "darin", "davon", "dazu", "daran", "damit", "darueber", "dabei", "sag", "sage",
        "sagt", "zeig", "zeige", "erklaer", "erklaere", "finde", "such", "suche", "lies", "lese", "datei", "dokument",
        "seite", "seiten", "text", "etwas", "alles", "viel", "viele", "sehr", "ganz", "ja", "nein", "gerade", "kannst",
        "koenntest", "mach", "mache", "gib", "nenn", "nenne", "fasse", "fass", "zusammen", "zusammenfassung", "worum",
        "wovon", "worueber", "drauf", "darauf", "wichtig", "wichtigste", "the", "an", "and", "or", "of", "to", "is",
        "are", "what", "which", "who", "how", "does", "do", "on", "for", "with", "it", "this", "that",
    ];

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
