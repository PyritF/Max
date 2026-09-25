using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Max.Ui;

/// <summary>
/// Zeigt Max beim Nachdenken zu – grau, kursiv, live:
/// <code>
///  ◆ ✻ Denkt nach… 4 s
///      Der Nutzer fragt nach dem Wetter. Ich habe keinen
///      Internetzugriff, also sollte ich …
/// </code>
/// Nur die letzten Zeilen sind zu sehen. Sobald die Antwort kommt, verschwindet alles wieder
/// (<see cref="Erase"/>) – die Antwort beginnt dann genau hinter dem ◆.
/// Gezeichnet wird relativ zum Cursor, wie bei der Eingabe: hochfahren, löschen, neu schreiben.
/// </summary>
internal sealed partial class ThinkingView(IAnsiConsole console)
{
    public const int VisibleLines = 6;
    private const int Indent = 5;
    private const int PrefixColumn = 3; // hinter " ◆ "

    private readonly StringBuilder _text = new();
    private readonly Stopwatch _clock = new();
    private readonly Lock _lock = new();
    private int _drawnLines;
    private bool _active;

    public bool IsActive => _active;

    public void Add(string text)
    {
        lock (_lock)
        {
            if (!_active)
            {
                _active = true;
                _clock.Start();
            }
            _text.Append(text);
            Draw();
        }
    }

    /// <summary>Nur die Sekunden weiterzählen (ohne neuen Text).</summary>
    public void Tick()
    {
        lock (_lock)
        {
            if (_active)
                Draw();
        }
    }

    /// <summary>Entfernt die Ansicht; der Cursor steht danach wieder direkt hinter dem ◆.</summary>
    public void Erase()
    {
        lock (_lock)
        {
            if (!_active)
                return;
            MoveToStart();
            console.WriteAnsi("\u001b[J");
            _active = false;
            _drawnLines = 0;
            _clock.Reset();
            _text.Clear();
        }
    }

    private void MoveToStart()
    {
        if (_drawnLines > 0)
            console.Cursor.MoveUp(_drawnLines);
        console.WriteAnsi("\r");
        console.WriteAnsi($"\u001b[{PrefixColumn}C");
    }

    private void Draw()
    {
        MoveToStart();
        console.WriteAnsi("\u001b[J");

        var width = Math.Max(20, console.Profile.Width - 1);
        var seconds = (int)_clock.Elapsed.TotalSeconds;
        console.Write("✻ ", new Style(Theme.Accent));
        console.Write($"Denkt nach… {seconds.ToString(CultureInfo.InvariantCulture)} s", new Style(Theme.Muted));

        var lines = LastLines(Clean(_text.ToString()), width - Indent, VisibleLines);
        var style = new Style(Theme.Muted, decoration: Decoration.Italic);
        foreach (var line in lines)
        {
            console.Write("\n" + new string(' ', Indent));
            console.Write(line, style);
        }
        _drawnLines = lines.Count;
    }

    /// <summary>Namen des zugrunde liegenden Modells tauchen im sichtbaren Nachdenken nicht auf.</summary>
    internal static string Clean(string text) => ModelNameRegex().Replace(text, "Max");

    /// <summary>Bricht den Text auf <paramref name="width"/> Spalten um und liefert die letzten Zeilen (ohne Leerzeilen).</summary>
    internal static List<string> LastLines(string text, int width, int count)
    {
        var rows = new List<string>();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var row = new StringBuilder();
            var used = 0;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w = CellWidth.OfText(word);
                if (used > 0 && used + 1 + w > width)
                {
                    rows.Add(row.ToString());
                    row.Clear();
                    used = 0;
                }
                if (w > width)
                {
                    rows.Add(ChoiceMenu.Fit(word, width));   // überlanges Wort: gekürzt
                    continue;
                }
                if (used > 0)
                {
                    row.Append(' ');
                    used++;
                }
                row.Append(word);
                used += w;
            }
            if (row.Length > 0)
                rows.Add(row.ToString());
        }
        return rows.Count <= count ? rows : rows.GetRange(rows.Count - count, count);
    }

    [GeneratedRegex("Qwen[0-9.]*|Alibaba|Tongyi|通义(千问)?", RegexOptions.IgnoreCase)]
    private static partial Regex ModelNameRegex();
}
