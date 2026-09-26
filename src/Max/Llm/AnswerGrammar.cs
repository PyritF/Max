using System.Text;
using Max.Ui;
using Max.Ui.Widgets;

namespace Max.Llm;

/// <summary>
/// Die Grammatik (GBNF) für Max' Antworten: Das Modell kann damit nur gültige Farb-Tags und
/// gültige Elemente schreiben. Freier Text bleibt frei – nur <c>{</c> und Backticks sind festgelegt:
/// <c>{…}</c> gibt es nur als bekanntes Tag, <c>```</c> nur mit bekannter Sprache (nie ohne) oder als Element,
/// dessen Inhalt zeilenweise vorgegeben ist (z. B. <c>balken</c>: mindestens eine Zeile "Name: Zahl").
/// Farben, Schriften und Elemente kommen aus dem Programm selbst, damit Grammatik und Anzeige zusammenpassen.
/// </summary>
internal static class AnswerGrammar
{
    /// <summary>Sprachen, die ein Code-Block haben darf (plus keine). Bewusst ohne Element-Namen.</summary>
    internal static readonly string[] CodeLanguages =
    [
        "c", "cpp", "c++", "h", "csharp", "cs", "c#", "java", "kotlin", "kt", "swift", "go", "rust", "rs",
        "python", "py", "javascript", "js", "typescript", "ts", "jsx", "tsx", "json", "jsonc", "yaml", "yml",
        "toml", "xml", "html", "css", "scss", "sql", "bash", "sh", "shell", "zsh", "powershell", "ps1", "pwsh",
        "bat", "cmd", "batch", "dockerfile", "docker", "makefile", "make", "ruby", "rb", "php", "perl", "lua", "r",
        "dart", "scala", "haskell", "elixir", "erlang", "clojure", "fsharp", "f#", "vb", "vbnet", "objc",
        "markdown", "md", "diff", "ini", "csv", "graphql",
        "proto", "regex", "latex", "tex", "asm", "matlab", "julia", "groovy", "gradle", "terraform", "hcl",
        "nginx", "http", "vim", "razor", "xaml", "svelte", "vue", "zig", "nim", "ocaml",
    ];

    /// <summary>Code-Blöcke für schlichten Text – darin sind Farb-Tags verboten (sie würden dort nicht wirken).</summary>
    internal static readonly string[] PlainLanguages = ["text", "txt", "plaintext", "console", "output", "log"];

    private static readonly Lazy<string> Cached = new(Build);

    public static string Gbnf => Cached.Value;

    internal static string Build()
    {
        var g = new StringBuilder();
        void Rule(string name, string body) => g.Append(name).Append(" ::= ").Append(body).Append('\n');

        Rule("root", "item*");
        Rule("item", "plain | tag | inline-code | fence");
        Rule("plain", "[^{`]");
        Rule("tag", "\"{\" ( \"/\"? color | \"verlauf\" ( \":\" grad )? | \"/verlauf\" ) \"}\"");
        Rule("inline-code", "\"`\" [^`\\n]+ \"`\"");
        Rule("fence", "\"```\" ( code | widget )");
        Rule("code", "lang \"\\n\" code-body | plain-lang \"\\n\" plain-body");
        Rule("code-body", "( [^`] | \"`\" [^`] | \"``\" [^`] )* \"```\"");
        Rule("plain-body", "( [^`{] | \"{\" [^a-zA-ZÀ-ɏ/`{] | \"`\" [^`{] | \"``\" [^`{] )* \"```\"");
        Rule("lang", Alternatives(CodeLanguages));
        Rule("plain-lang", Alternatives(PlainLanguages));

        Rule("color", Alternatives(ColorTags.Names));
        Rule("grad", "color \"-\" color");
        Rule("nl", "\"\\n\"");
        Rule("value", "[^\\n`]{1,120}");
        Rule("content-line", "[^`\\n]+ nl");
        Rule("setting", "\"Titel: \" value nl | \"Einheit: \" value nl | \"Verlauf: \" grad nl | \"Farbe: \" color nl");
        Rule("num-line", "( \"- \" )? label \": \" number unit nl");
        // Kurze Namen ohne "Spalten": höchstens einzelne Leerzeichen zwischen Wörtern, keine Tabs.
        Rule("label", "[^-:|\\n\\t`{ ] ( [^:|\\n\\t`{ ] | \" \" [^:|\\n\\t`{ ] ){0,24}");
        Rule("number", "\"-\"? [0-9]+ ( [.,] [0-9]+ )*");
        // Einheit ("%", " Mio. €") – ohne weitere Zahlen oder Doppelpunkte, sonst stünden mehrere Werte in einer Zeile;
        // direkt an der Zahl nur ein Zeichen wie % oder €, sonst mit Leerzeichen ("1. Wien" ist keine Zahl mit Einheit).
        Rule("unit", "( [%\u20ac$\u00b0] | \" \" [^0-9:\\n`|{ ] [^0-9:\\n`|{]{0,10} )?");
        Rule("opt", "\"- \" value nl");
        Rule("day", "[0-9] [0-9]?");
        Rule("font", Alternatives(TitleWidget.BuiltInFonts.Append("standard").Distinct()));

        var numeric = new[] { "balken", "anteile", "fortschritt" };
        Rule("widget", "w-num | w-kurve | w-frage | w-titel | w-kalender | w-kasten | w-baum | w-spalten");
        Rule("w-num", $"( {Alternatives(numeric)} ) nl setting* num-line ( num-line | setting )* \"```\"");
        Rule("w-kurve", "\"kurve\" nl setting* num-line setting* num-line setting* num-line ( num-line | setting )* \"```\"");
        Rule("w-frage", $"( {Alternatives(ChoiceQuestion.BlockNames.Select(n => n.ToLowerInvariant()).Distinct())} ) nl \"Frage: \" value nl opt opt opt? opt? opt? \"```\"");
        Rule("w-titel", "\"titel\" nl \"Text: \" value nl ( \"Schrift: \" font nl )? ( \"Verlauf: \" grad nl | \"Farbe: \" color nl )? \"```\"");
        Rule("w-kalender", "\"kalender\" nl ( \"Titel: \" value nl )? \"Monat: \" [0-9] [0-9] [0-9] [0-9] \"-\" [0-9] [0-9] nl ( \"Markiert: \" day ( \", \" day )* nl )? \"```\"");
        Rule("w-kasten", "\"kasten\" nl setting* nl* content-line ( content-line | nl )* \"```\"");
        Rule("w-baum", "\"baum\" nl setting* content-line ( content-line | nl )* \"```\"");
        Rule("w-spalten", "\"spalten\" nl setting* content-line ( content-line | nl )* \"---\" nl ( content-line | nl )* content-line ( content-line | nl )* \"```\"");
        return AsciiOnly(g.ToString());
    }

    /// <summary>
    /// Schreibt jedes Nicht-ASCII-Zeichen als <c>\uXXXX</c> (GBNF versteht das in Texten und Zeichenklassen).
    /// Nötig, weil LLamaSharp die Grammatik unter Windows in der ANSI-Codepage übergibt – Umlaute würden
    /// dort zu "?", llama.cpp könnte sie nicht lesen, und der kaputte Sampler brächte Max zum Absturz.
    /// </summary>
    internal static string AsciiOnly(string gbnf)
    {
        var output = new StringBuilder(gbnf.Length);
        foreach (var rune in gbnf.EnumerateRunes())
        {
            if (rune.Value < 128)
                output.Append((char)rune.Value);
            else if (rune.Value <= 0xFFFF)
                output.Append($"\\u{rune.Value:x4}");
            else
                output.Append($"\\U{rune.Value:x8}");
        }
        return output.ToString();
    }

    private static string Alternatives(IEnumerable<string> words) =>
        string.Join(" | ", words.Select(w => "\"" + w.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
}
