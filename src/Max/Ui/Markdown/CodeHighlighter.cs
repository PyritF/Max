using Spectre.Console;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TmTheme = TextMateSharp.Themes.Theme;

namespace Max.Ui.Markdown;

/// <summary>
/// Syntax-Hervorhebung für Code-Blöcke mit den Grammatiken aus VS Code (TextMateSharp).
/// Arbeitet Zeile für Zeile und merkt sich den Zustand – mehrzeilige Kommentare und Strings
/// bleiben so über Zeilen hinweg richtig gefärbt. Unbekannte Sprache → schlicht.
/// </summary>
internal sealed class CodeHighlighter
{
    private static readonly Lazy<(RegistryOptions Options, Registry Registry, TmTheme Theme)?> Engine = new(() =>
    {
        try
        {
            var options = new RegistryOptions(ThemeName.DarkPlus);
            var registry = new Registry(options);
            return (options, registry, registry.GetTheme());
        }
        catch
        {
            return null; // native Regex-Bibliothek fehlt o. Ä. – dann eben ohne Farben
        }
    });

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "csharp", ["c#"] = "csharp", ["py"] = "python", ["js"] = "javascript", ["ts"] = "typescript",
        ["sh"] = "shellscript", ["bash"] = "shellscript", ["shell"] = "shellscript", ["zsh"] = "shellscript",
        ["ps"] = "powershell", ["ps1"] = "powershell", ["pwsh"] = "powershell", ["yml"] = "yaml",
        ["c++"] = "cpp", ["rs"] = "rust", ["golang"] = "go", ["kt"] = "kotlin", ["md"] = "markdown",
        ["htm"] = "html", ["jsonc"] = "json", ["dockerfile"] = "docker", ["bat"] = "bat", ["cmd"] = "bat",
    };

    private static readonly Dictionary<string, IGrammar?> Grammars = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock GrammarLock = new();

    private readonly IGrammar? _grammar;
    private readonly Style _plain;
    private IStateStack? _state;

    public CodeHighlighter(string language, Style plain)
    {
        _plain = plain;
        _grammar = language.Length == 0 ? null : LoadGrammar(language);
    }

    public bool IsActive => _grammar is not null;

    /// <summary>Eine Zeile in farbige Stücke zerlegen.</summary>
    public IEnumerable<(string Text, Style Style)> Highlight(string line)
    {
        if (_grammar is null || Engine.Value is not { } engine || line.Length == 0)
            return [(line, _plain)];

        try
        {
            var result = _grammar.TokenizeLine(line, _state, TimeSpan.FromMilliseconds(50));
            _state = result.RuleStack;
            var parts = new List<(string, Style)>();
            foreach (var token in result.Tokens)
            {
                var start = Math.Min(token.StartIndex, line.Length);
                var end = Math.Min(token.EndIndex, line.Length);
                if (end <= start)
                    continue;
                parts.Add((line[start..end], StyleFor(engine.Theme, token.Scopes)));
            }
            return parts.Count > 0 ? parts : [(line, _plain)];
        }
        catch
        {
            return [(line, _plain)];
        }
    }

    private Style StyleFor(TmTheme theme, List<string> scopes)
    {
        var rule = theme.Match(scopes).FirstOrDefault(r => r.foreground > 0);
        if (rule is null)
            return _plain;
        var decoration = Decoration.None;
        if (rule.fontStyle > 0 && ((int)rule.fontStyle & 1) != 0)
            decoration |= Decoration.Italic;
        if (rule.fontStyle > 0 && ((int)rule.fontStyle & 2) != 0)
            decoration |= Decoration.Bold;
        return ParseHex(theme.GetColor(rule.foreground)) is { } color ? new Style(color, decoration: decoration) : _plain;
    }

    private static Color? ParseHex(string? hex)
    {
        if (hex is null || hex.Length < 7 || hex[0] != '#')
            return null;
        try
        {
            return new Color(Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IGrammar? LoadGrammar(string language)
    {
        lock (GrammarLock)
        {
            if (Grammars.TryGetValue(language, out var cached))
                return cached;

            IGrammar? grammar = null;
            if (Engine.Value is { } engine)
            {
                try
                {
                    var id = Aliases.GetValueOrDefault(language, language.ToLowerInvariant());
                    var scope = engine.Options.GetScopeByLanguageId(id) ?? engine.Options.GetScopeByExtension("." + language.ToLowerInvariant());
                    if (scope is not null)
                        grammar = engine.Registry.LoadGrammar(scope);
                }
                catch
                {
                    grammar = null;
                }
            }
            Grammars[language] = grammar;
            return grammar;
        }
    }
}
