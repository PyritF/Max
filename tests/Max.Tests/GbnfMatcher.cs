namespace Max.Tests;

/// <summary>
/// Prüft in den Tests, ob ein ganzer Text zu einer GBNF-Grammatik passt – so, wie llama.cpp sie liest: Texte,
/// Zeichenklassen, Regeln, Gruppen, <c>* + ?</c> und <c>{m,n}</c>. Max selbst gibt die Grammatik nur an llama.cpp weiter;
/// ohne Modell lässt sich dort aber nicht nachsehen, was sie erlaubt.
/// </summary>
internal sealed class GbnfMatcher
{
    private abstract class Node;

    private sealed class Literal(int[] text) : Node
    {
        public int[] Text { get; } = text;
    }

    private sealed class CharClass(List<(int Low, int High)> ranges, bool negated) : Node
    {
        public bool Matches(int c) => ranges.Any(r => c >= r.Low && c <= r.High) != negated;
    }

    private sealed class RuleRef(string name) : Node
    {
        public string Name { get; } = name;
    }

    private sealed class Sequence(List<Node> items) : Node
    {
        public List<Node> Items { get; } = items;
    }

    private sealed class Choice(List<Node> options) : Node
    {
        public List<Node> Options { get; } = options;
    }

    private sealed class Repeat(Node inner, int min, int max) : Node
    {
        public Node Inner { get; } = inner;
        public int Min { get; } = min;
        public int Max { get; } = max;
    }

    private readonly Dictionary<string, Node> _rules = [];

    public GbnfMatcher(string gbnf)
    {
        foreach (var line in gbnf.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = line.IndexOf(" ::= ", StringComparison.Ordinal);
            _rules[line[..split]] = new Parser(line[(split + 5)..]).ParseAll();
        }
    }

    /// <summary>Passt der ganze Text (nicht nur ein Anfang davon) zur Regel?</summary>
    public bool Accepts(string text, string rule = "root")
    {
        var input = text.EnumerateRunes().Select(r => r.Value).ToArray();
        return Ends(_rules[rule], input, 0, []).Contains(input.Length);
    }

    /// <summary>Alle Stellen, an denen <paramref name="node"/> ab <paramref name="pos"/> enden kann.</summary>
    private HashSet<int> Ends(Node node, int[] input, int pos, Dictionary<(Node, int), HashSet<int>> memo)
    {
        if (memo.TryGetValue((node, pos), out var known))
            return known;
        HashSet<int> result;
        switch (node)
        {
            case Literal literal:
                result = pos + literal.Text.Length <= input.Length && input.AsSpan(pos, literal.Text.Length).SequenceEqual(literal.Text)
                    ? [pos + literal.Text.Length] : [];
                break;
            case CharClass chars:
                result = pos < input.Length && chars.Matches(input[pos]) ? [pos + 1] : [];
                break;
            case RuleRef reference:
                memo[(node, pos)] = [];             // Schutz vor Linksrekursion
                result = Ends(_rules[reference.Name], input, pos, memo);
                break;
            case Sequence sequence:
                result = [pos];
                foreach (var item in sequence.Items)
                {
                    var next = new HashSet<int>();
                    foreach (var p in result)
                        next.UnionWith(Ends(item, input, p, memo));
                    result = next;
                    if (result.Count == 0)
                        break;
                }
                break;
            case Choice choice:
                result = [];
                foreach (var option in choice.Options)
                    result.UnionWith(Ends(option, input, pos, memo));
                break;
            case Repeat repeat:
                result = repeat.Min == 0 ? [pos] : [];
                var frontier = new HashSet<int> { pos };
                var expanded = new HashSet<int>();
                for (var count = 1; count <= repeat.Max && frontier.Count > 0; count++)
                {
                    var next = new HashSet<int>();
                    foreach (var p in frontier)
                    {
                        // Über dem Minimum führt dieselbe Stelle immer zu denselben (oder weniger) Enden.
                        if (count > repeat.Min && !expanded.Add(p))
                            continue;
                        next.UnionWith(Ends(repeat.Inner, input, p, memo));
                    }
                    if (count >= repeat.Min)
                        result.UnionWith(next);
                    frontier = next;
                }
                break;
            default:
                throw new InvalidOperationException(node.GetType().Name);
        }
        memo[(node, pos)] = result;
        return result;
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public Node ParseAll()
        {
            var node = ParseChoice();
            SkipSpace();
            if (_pos != text.Length)
                throw new FormatException($"Unerwartet: {text[_pos..]}");
            return node;
        }

        private Node ParseChoice()
        {
            var options = new List<Node> { ParseSequence() };
            while (_pos < text.Length && text[_pos] == '|')
            {
                _pos++;
                options.Add(ParseSequence());
            }
            return options.Count == 1 ? options[0] : new Choice(options);
        }

        private Node ParseSequence()
        {
            var items = new List<Node>();
            while (true)
            {
                SkipSpace();
                if (_pos >= text.Length || text[_pos] is '|' or ')')
                    break;
                items.Add(ParsePostfix(ParsePrimary()));
            }
            return items.Count == 1 ? items[0] : new Sequence(items);
        }

        private Node ParsePrimary()
        {
            switch (text[_pos])
            {
                case '"':
                    _pos++;
                    var literal = new List<int>();
                    while (text[_pos] != '"')
                        literal.Add(ReadChar());
                    _pos++;
                    return new Literal([.. literal]);
                case '[':
                    _pos++;
                    var negated = text[_pos] == '^';
                    if (negated)
                        _pos++;
                    var ranges = new List<(int, int)>();
                    while (text[_pos] != ']')
                    {
                        var low = ReadChar();
                        var high = low;
                        if (text[_pos] == '-' && text[_pos + 1] != ']')
                        {
                            _pos++;
                            high = ReadChar();
                        }
                        ranges.Add((low, high));
                    }
                    _pos++;
                    return new CharClass(ranges, negated);
                case '(':
                    _pos++;
                    var inner = ParseChoice();
                    SkipSpace();
                    if (text[_pos] != ')')
                        throw new FormatException($"')' fehlt: {text[_pos..]}");
                    _pos++;
                    return inner;
                default:
                    var start = _pos;
                    while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] is '-' or '_'))
                        _pos++;
                    if (start == _pos)
                        throw new FormatException($"Unerwartet: {text[_pos..]}");
                    return new RuleRef(text[start.._pos]);
            }
        }

        private Node ParsePostfix(Node node)
        {
            while (_pos < text.Length)
            {
                switch (text[_pos])
                {
                    case '*':
                        _pos++;
                        node = new Repeat(node, 0, int.MaxValue);
                        break;
                    case '+':
                        _pos++;
                        node = new Repeat(node, 1, int.MaxValue);
                        break;
                    case '?':
                        _pos++;
                        node = new Repeat(node, 0, 1);
                        break;
                    case '{':
                        var close = text.IndexOf('}', _pos);
                        var bounds = text[(_pos + 1)..close].Split(',');
                        var min = int.Parse(bounds[0]);
                        var max = bounds.Length == 1 ? min : bounds[1].Length == 0 ? int.MaxValue : int.Parse(bounds[1]);
                        node = new Repeat(node, min, max);
                        _pos = close + 1;
                        break;
                    default:
                        return node;
                }
            }
            return node;
        }

        private int ReadChar()
        {
            var c = text[_pos++];
            if (c != '\\')
                return c;
            var escaped = text[_pos++];
            return escaped switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'x' => Hex(2),
                'u' => Hex(4),
                'U' => Hex(8),
                _ => escaped,
            };
        }

        private int Hex(int digits)
        {
            var value = Convert.ToInt32(text.Substring(_pos, digits), 16);
            _pos += digits;
            return value;
        }

        private void SkipSpace()
        {
            while (_pos < text.Length && text[_pos] == ' ')
                _pos++;
        }
    }
}
