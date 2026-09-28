using System.Globalization;

namespace Max.Tools;

/// <summary>
/// Rechnet exakt: + − × ÷, Klammern, Potenzen (^), Prozent (%), sqrt/wurzel, abs.
/// Mit <see cref="decimal"/> (28 Stellen) – so stimmen auch große Produkte auf die letzte Stelle.
/// Wird es selbst dafür zu groß, mit <see cref="double"/> und dem Hinweis "ungefähr".
/// </summary>
internal sealed class CalculatorTool : ITool
{
    public string Name => "rechnen";
    public string? Argument => "Rechnung, z. B. 17.5 * (3 + 4)^2";
    public string Description => "Rechnet exakt. Nimm es für jede Rechnung mit mehr als kleinen Zahlen, statt im Kopf zu rechnen.";
    public string Describe(string argument) => $"Rechne: {argument}";

    public Task<string> RunAsync(string argument, CancellationToken ct)
    {
        try
        {
            return Task.FromResult($"{argument} = {Evaluate(argument)}");
        }
        catch (FormatException e)
        {
            return Task.FromResult($"Das kann ich so nicht rechnen: {e.Message}");
        }
        catch (DivideByZeroException)
        {
            return Task.FromResult("Division durch null.");
        }
    }

    /// <summary>Das Ergebnis als Text, z. B. "121932631112635269" oder "0.333333333333".</summary>
    internal static string Evaluate(string expression)
    {
        var node = new Parser(expression).ParseAll();
        try
        {
            var value = node.Exact();
            value = Math.Round(value, 12);
            return value.ToString("0.############", CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            var value = node.Approx();
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new FormatException("Das Ergebnis ist zu groß oder nicht definiert.");
            return "ungefähr " + value.ToString("G15", CultureInfo.InvariantCulture);
        }
    }

    private abstract class Node
    {
        public abstract decimal Exact();
        public abstract double Approx();
    }

    private sealed class Number(decimal value) : Node
    {
        public override decimal Exact() => value;
        public override double Approx() => (double)value;
    }

    private sealed class Unary(char op, Node operand) : Node
    {
        public override decimal Exact() => op switch
        {
            '-' => -operand.Exact(),
            '%' => operand.Exact() / 100,
            _ => operand.Exact(),
        };

        public override double Approx() => op switch
        {
            '-' => -operand.Approx(),
            '%' => operand.Approx() / 100,
            _ => operand.Approx(),
        };
    }

    private sealed class Binary(char op, Node left, Node right) : Node
    {
        public override decimal Exact()
        {
            var a = left.Exact();
            var b = right.Exact();
            return op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => a / b,
                '^' => Power(a, b),
                _ => throw new FormatException($"Unbekanntes Zeichen '{op}'."),
            };
        }

        public override double Approx()
        {
            var a = left.Approx();
            var b = right.Approx();
            return op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => b == 0 ? throw new DivideByZeroException() : a / b,
                '^' => Math.Pow(a, b),
                _ => throw new FormatException($"Unbekanntes Zeichen '{op}'."),
            };
        }

        /// <summary>Ganze Exponenten exakt, alles andere über double.</summary>
        private static decimal Power(decimal a, decimal b)
        {
            if (b != Math.Floor(b) || Math.Abs(b) > 1000)
                return (decimal)Math.Pow((double)a, (double)b);
            var result = 1m;
            for (var i = 0; i < Math.Abs(b); i++)
                result *= a;
            return b < 0 ? 1 / result : result;
        }
    }

    private sealed class Function(string name, Node argument) : Node
    {
        public override decimal Exact() => name switch
        {
            "abs" => Math.Abs(argument.Exact()),
            _ => (decimal)Approx(),
        };

        public override double Approx()
        {
            var x = argument.Approx();
            return name switch
            {
                "sqrt" or "wurzel" => x < 0 ? throw new FormatException("Wurzel aus einer negativen Zahl.") : Math.Sqrt(x),
                "abs" => Math.Abs(x),
                "ln" => Math.Log(x),
                "log" => Math.Log10(x),
                "sin" => Math.Sin(x),
                "cos" => Math.Cos(x),
                "tan" => Math.Tan(x),
                _ => throw new FormatException($"Die Funktion '{name}' kenne ich nicht."),
            };
        }
    }

    /// <summary>Rekursiver Abstieg: Summe → Produkt → Potenz → Vorzeichen → Zahl/Klammer/Funktion.</summary>
    private sealed class Parser(string text)
    {
        private readonly string _text = Normalize(text);
        private int _pos;

        public Node ParseAll()
        {
            var node = Sum();
            Skip();
            if (_pos < _text.Length)
                throw new FormatException($"Unerwartetes '{_text[_pos]}' an Stelle {_pos + 1}.");
            return node;
        }

        private static string Normalize(string text) => text
            .Replace('×', '*').Replace('·', '*').Replace('÷', '/').Replace(':', '/').Replace('−', '-')
            .Replace("**", "^");

        private Node Sum()
        {
            var node = Product();
            while (Peek() is '+' or '-')
                node = new Binary(Next(), node, Product());
            return node;
        }

        private Node Product()
        {
            var node = Power();
            while (Peek() is '*' or '/')
                node = new Binary(Next(), node, Power());
            return node;
        }

        private Node Power()
        {
            var node = Signed();
            if (Peek() == '^')
            {
                Next();
                return new Binary('^', node, Power());      // rechtsassoziativ: 2^3^2 = 2^9
            }
            return node;
        }

        private Node Signed()
        {
            if (Peek() is '-' or '+')
                return new Unary(Next(), Signed());
            return Percent(Atom());
        }

        private Node Percent(Node node)
        {
            while (Peek() == '%')
            {
                Next();
                node = new Unary('%', node);
            }
            return node;
        }

        private Node Atom()
        {
            Skip();
            if (_pos >= _text.Length)
                throw new FormatException("Die Rechnung hört mittendrin auf.");
            var c = _text[_pos];
            if (c == '(')
            {
                _pos++;
                var inner = Sum();
                if (Next() != ')')
                    throw new FormatException("Eine Klammer wird nicht geschlossen.");
                return inner;
            }
            if (char.IsDigit(c) || c is '.' or ',')
                return new Number(ReadNumber());
            if (char.IsLetter(c))
            {
                var start = _pos;
                while (_pos < _text.Length && char.IsLetter(_text[_pos]))
                    _pos++;
                var name = _text[start.._pos].ToLowerInvariant();
                if (name == "pi")
                    return new Number(3.14159265358979323846264338m);
                if (Peek() != '(')
                    throw new FormatException($"'{name}' verstehe ich nicht.");
                return new Function(name, Atom());
            }
            throw new FormatException($"Unerwartetes '{c}' an Stelle {_pos + 1}.");
        }

        /// <summary>"3.5", "3,5" und "1.000.000" (mehrere Punkte = Tausender).</summary>
        private decimal ReadNumber()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] is '.' or ',' or '_' or '\''))
                _pos++;
            var raw = _text[start.._pos].Replace("_", "").Replace("'", "");
            if (raw.Count(ch => ch == '.') > 1)
                raw = raw.Replace(".", "");
            if (raw.Count(ch => ch == ',') > 1)
                raw = raw.Replace(",", "");
            raw = raw.Replace(',', '.');
            if (!decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"'{raw}' ist keine Zahl.");
            return value;
        }

        private char Peek()
        {
            Skip();
            return _pos < _text.Length ? _text[_pos] : '\0';
        }

        private char Next()
        {
            Skip();
            return _pos < _text.Length ? _text[_pos++] : '\0';
        }

        private void Skip()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }
    }
}
