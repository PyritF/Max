using System.Text;
using System.Text.RegularExpressions;
using Max.Ui.Widgets;

namespace Max.Llm;

/// <summary>
/// Hält Element-Blöcke (<c>```balken</c> … <c>```</c>) im Antwort-Strom zurück, bis sie vollständig sind –
/// dann entscheidet das Backend: zeigen oder unsichtbar neu erzeugen lassen. Für die Anzeige ändert sich nichts,
/// denn Elemente werden ohnehin erst gezeichnet, wenn der Block zu ist. Normaler Text fließt sofort durch;
/// nur eine Zeile, die mit einem Backtick beginnt, wartet bis zum Zeilenende (könnte ein Element werden).
/// </summary>
internal sealed partial class ElementGate
{
    private readonly StringBuilder _line = new();   // zurückgehaltene Zeile, die mit ` beginnt
    private readonly StringBuilder _held = new();   // zurückgehaltener Element-Block (Kopfzeile bis jetzt)
    private readonly StringBuilder _body = new();
    private readonly StringBuilder _rest = new();   // Text nach einem geschlossenen Block, bis entschieden ist
    private bool _atLineStart = true;
    private bool _holdingLine;
    private string? _element;

    /// <summary>Ein vollständiger Element-Block, über den entschieden werden muss (<see cref="Accept"/> oder <see cref="Drop"/>).</summary>
    public (string Name, string Body)? Closed { get; private set; }

    /// <summary>Gerade in einem Element-Block (nach der Kopfzeile)?</summary>
    public bool InElement => _element is not null && Closed is null;

    /// <summary>Nimmt Antwort-Text an und liefert, was schon angezeigt werden darf.</summary>
    public string Push(string text)
    {
        if (Closed is not null)
        {
            _rest.Append(text);                         // erst entscheiden, dann weiter
            return "";
        }

        var output = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (_element is not null)
            {
                _held.Append(c);
                if (c != '\n')
                {
                    _line.Append(c);
                    continue;
                }
                var line = _line.ToString();
                _line.Clear();
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    Closed = (_element, _body.ToString());
                    _rest.Append(text, i + 1, text.Length - i - 1);
                    return output.ToString();
                }
                _body.Append(line).Append('\n');
                continue;
            }

            if (_holdingLine)
            {
                _line.Append(c);
                if (c == '\n')
                    CompleteHeldLine(output);
                continue;
            }

            if (_atLineStart && c == '`')
            {
                _holdingLine = true;
                _line.Append(c);
                _atLineStart = false;
                continue;
            }

            output.Append(c);
            _atLineStart = c == '\n';
        }
        return output.ToString();
    }

    /// <summary>
    /// Würde dieser Text die Kopfzeile eines Elements abschließen? Dann lohnt ein Zwischenstand
    /// (vor dem Token), um den Block bei Bedarf neu erzeugen zu können. Ändert nichts.
    /// </summary>
    public bool WouldOpenElement(string text)
    {
        if (_element is not null || Closed is not null)
            return false;
        var newline = text.IndexOf('\n');
        if (newline < 0)
            return false;
        string line;
        if (_holdingLine)
            line = _line + text[..newline];
        else if (_atLineStart && text.StartsWith('`'))
            line = text[..newline];
        else
            return false;
        return HeaderRegex().Match(line) is { Success: true } m && WidgetValidator.IsElement(m.Groups[1].Value.ToLowerInvariant());
    }

    /// <summary>Block ist gut: Er und der Text danach dürfen raus.</summary>
    public string Accept()
    {
        var output = _held.ToString();
        return output + Continue();
    }

    /// <summary>Block wird weggelassen; der Text danach darf raus.</summary>
    public string Drop() => Continue();

    /// <summary>Stand nach der Kopfzeile eines Elements – um nach dem Neu-Erzeugen dort weiterzumachen.</summary>
    public Snapshot Save() => new(_element, _held.ToString(), _body.ToString(), _line.ToString());

    public void Load(Snapshot snapshot)
    {
        _element = snapshot.Element;
        _held.Clear().Append(snapshot.Held);
        _body.Clear().Append(snapshot.Body);
        _line.Clear().Append(snapshot.Line);
        _rest.Clear();
        Closed = null;
        _holdingLine = false;
        _atLineStart = false;
    }

    /// <summary>Am Ende: Offenes zurückgeben. Ein nicht geschlossener Element-Block wird als geschlossen gemeldet.</summary>
    public string Flush()
    {
        if (_element is not null && Closed is null)
        {
            if (_line.Length > 0)
                _body.Append(_line);
            _line.Clear();
            Closed = (_element, _body.ToString());
            return "";
        }
        var rest = _line.ToString();
        _line.Clear();
        _holdingLine = false;
        return rest;
    }

    private string Continue()
    {
        var rest = _rest.ToString();
        _element = null;
        _held.Clear();
        _body.Clear();
        _line.Clear();
        _rest.Clear();
        Closed = null;
        _holdingLine = false;
        _atLineStart = true;
        return Push(rest);
    }

    private void CompleteHeldLine(StringBuilder output)
    {
        var line = _line.ToString();
        _line.Clear();
        _holdingLine = false;
        _atLineStart = true;

        if (HeaderRegex().Match(line.TrimEnd('\n', '\r')) is { Success: true } m
            && WidgetValidator.IsElement(m.Groups[1].Value.ToLowerInvariant()))
        {
            _element = m.Groups[1].Value.ToLowerInvariant();
            _held.Append(line);
            _body.Clear();
            return;
        }
        output.Append(line);
    }

    internal sealed record Snapshot(string? Element, string Held, string Body, string Line);

    [GeneratedRegex(@"^\s*```\s*([\p{L}]+)\s*$")]
    private static partial Regex HeaderRegex();
}
