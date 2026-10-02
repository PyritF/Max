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
    private readonly StringBuilder _probe = new();  // Kopfzeile eines Code-Blocks mitten in der Antwort, bis die erste Zeile da ist
    private bool _atLineStart = true;
    private bool _holdingLine;
    private string? _element;
    private bool _started;                          // schon etwas Sichtbares durchgelassen?
    private bool _probing;                          // Code-Block mitten in der Antwort: Ist die erste Zeile ein Aufruf?
    private bool _inCode;                           // in einem normal gezeigten Code-Block (dessen ``` schließt nur)
    private char _lastVisible;                      // letztes sichtbares Zeichen, das schon durchgelassen wurde

    /// <summary>
    /// Einen Code-Block ganz am Anfang der Antwort zurückhalten (als <see cref="Tools.ToolCall.MaybeBlockName"/>):
    /// Das Modell schreibt Werkzeug-Aufrufe gern als "```python" mit "datei: README.md" darin. Ob es einer ist,
    /// entscheidet das Backend, wenn der Block zu ist – sonst wird er ganz normal als Code gezeigt.
    /// </summary>
    public bool HoldFirstFence { get; init; }

    /// <summary>
    /// Ist diese Zeile ein Werkzeug-Aufruf ("websuche: Wien")? Dann wird auch ein Code-Block mitten in der Antwort
    /// zurückgehalten, dessen erste Zeile so aussieht – das Modell kündigt die Suche manchmal erst an ("Ich sehe
    /// nach.") und schreibt den Aufruf dann in einen "```bash"-Block. Null: nur der erste Block (<see cref="HoldFirstFence"/>).
    /// </summary>
    public Func<string, bool>? IsCallLine { get; init; }

    /// <summary>Ein vollständiger Element-Block, über den entschieden werden muss (<see cref="Accept"/> oder <see cref="Drop"/>).</summary>
    public (string Name, string Body)? Closed { get; private set; }

    /// <summary>Gerade in einem Element-Block (nach der Kopfzeile)?</summary>
    public bool InElement => _element is not null && Closed is null;

    /// <summary>Der Name des offenen Element-Blocks (oder null).</summary>
    public string? Element => Closed is null ? _element : null;

    /// <summary>
    /// Den offenen Block doch nicht zurückhalten, sondern als Text weiterfließen lassen – für einen Code-Block am
    /// Anfang, der zu lang für einen Werkzeug-Aufruf ist.
    /// </summary>
    public string Release()
    {
        var output = _held.ToString();
        _element = null;
        _held.Clear();
        _body.Clear();
        _line.Clear();
        _started = true;
        _inCode = true;                             // der Block geht als Code weiter
        _atLineStart = output.EndsWith('\n');
        return output;
    }

    /// <summary>Wie viel vom offenen Element-Block schon zurückgehalten ist (gegen Endlosschleifen).</summary>
    public int HeldLength => _held.Length;

    /// <summary>Der zurückgehaltene Element-Block bis jetzt.</summary>
    public string HeldText => _held.ToString();

    /// <summary>Den offenen Element-Block verwerfen, ohne dass etwas herauskommt.</summary>
    public void Abandon()
    {
        Continue();
        _rest.Clear();
    }

    /// <summary>Block und alles danach verwerfen – die Antwort geht vor dem Block neu weiter.</summary>
    public void Reset()
    {
        _rest.Clear();
        Continue();
    }

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
                    Remember(output);
                    return output.ToString();
                }
                _body.Append(line).Append('\n');
                continue;
            }

            if (_probing)
            {
                _probe.Append(c);
                if (c != '\n')
                {
                    _line.Append(c);
                    continue;
                }
                var line = _line.ToString();
                _line.Clear();
                var content = line.Trim();
                if (content.Length == 0 || content.Equals(Tools.ToolCall.BlockName, StringComparison.OrdinalIgnoreCase))
                    continue;                           // noch keine Zeile mit Inhalt
                _probing = false;
                var closes = content.StartsWith("```", StringComparison.Ordinal);
                if (!closes && IsCallLine!(content))
                {
                    // Ein Aufruf: zurückhalten wie einen Block am Anfang – das Backend entscheidet, wenn er zu ist.
                    _element = Tools.ToolCall.MaybeBlockName;
                    _held.Append(_probe);
                    _body.Clear().Append(line).Append('\n');
                    _probe.Clear();
                    continue;
                }
                output.Append(_probe);
                _probe.Clear();
                _inCode = !closes;
                _atLineStart = true;
                _started = true;
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
            _started |= !char.IsWhiteSpace(c);
        }
        Remember(output);
        return output.ToString();
    }

    private void Remember(StringBuilder output)
    {
        for (var i = output.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(output[i]))
            {
                _lastVisible = output[i];
                return;
            }
        }
    }

    /// <summary>Endet der Text vor dieser Stelle mit einer Frage? Ein Block danach ist ein Beispiel, kein Aufruf.</summary>
    private bool AfterQuestion(StringBuilder output)
    {
        for (var i = output.Length - 1; i >= 0; i--)
            if (!char.IsWhiteSpace(output[i]))
                return output[i] == '?';
        return _lastVisible == '?';
    }

    /// <summary>
    /// Würde dieser Text die Kopfzeile eines Elements abschließen? Dann lohnt ein Zwischenstand
    /// (vor dem Token), um den Block bei Bedarf neu erzeugen zu können. Ändert nichts.
    /// </summary>
    public bool WouldOpenElement(string text)
    {
        if (_element is not null || Closed is not null || _probing)
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
        _started = true;
        _lastVisible = '`';                         // der Block endet mit ```
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
        _probe.Clear();
        Closed = null;
        _holdingLine = false;
        _probing = false;
        _inCode = false;
        _atLineStart = false;
    }

    /// <summary>Am Ende: Offenes zurückgeben. Ein nicht geschlossener Element-Block wird als geschlossen gemeldet.</summary>
    public string Flush()
    {
        if (_probing)
        {
            // Der Block endet ohne Zeilenumbruch nach dem Aufruf ("```bash" / "websuche: Wien") – gilt trotzdem.
            _probing = false;
            var content = _line.ToString().Trim();
            if (content.Length > 0 && !content.StartsWith("```", StringComparison.Ordinal) && IsCallLine!(content))
            {
                _element = Tools.ToolCall.MaybeBlockName;
                _held.Append(_probe).Append(_line);
                _body.Clear().Append(_line);
                _probe.Clear();
                _line.Clear();
                Closed = (_element, _body.ToString());
                return "";
            }
            var probe = _probe.ToString() + _line;
            _probe.Clear();
            _line.Clear();
            return probe;
        }
        if (_element is not null && Closed is null)
        {
            // Am Ende fehlt oft nur der Zeilenumbruch nach dem schließenden ``` – das gehört nicht zum Inhalt.
            if (_line.Length > 0 && !_line.ToString().TrimStart().StartsWith("```", StringComparison.Ordinal))
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
        _probe.Clear();
        Closed = null;
        _holdingLine = false;
        _probing = false;
        _inCode = false;                            // ein zurückgehaltener Block ist immer ganz (oder ganz weg)
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
        var fence = line.TrimStart().StartsWith("```", StringComparison.Ordinal);
        if (HoldFirstFence && !_started && fence)
        {
            _element = Tools.ToolCall.MaybeBlockName;
            _held.Append(line);
            _body.Clear();
            return;
        }
        // Nach einer Frage ("Willst du eine Datei ansehen?") ist ein Block ein Beispiel, kein Aufruf (Selbsttest 55).
        if (IsCallLine is not null && !_inCode && fence && !AfterQuestion(output))
        {
            _probing = true;                        // erst die erste Zeile abwarten
            _probe.Append(line);
            return;
        }
        output.Append(line);
        if (fence)
            _inCode = !_inCode;
        _started |= line.Trim().Length > 0;
    }

    internal sealed record Snapshot(string? Element, string Held, string Body, string Line);

    [GeneratedRegex(@"^\s*```\s*([\p{L}]+)\s*$")]
    private static partial Regex HeaderRegex();
}
