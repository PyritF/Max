using System.Globalization;
using System.Text;
using Spectre.Console;

namespace Max.Ui;

/// <summary>
/// Die Eingabe unten: Linie, Text (wächst mit), Linie und Statuszeile darunter.
/// Die Tasten verarbeitet <see cref="LineEditor"/>; hier wird nur gezeichnet – bei jeder Änderung
/// der ganze Kasten neu, relativ zur Cursorposition. Nach dem Senden entfernt <see cref="Erase"/>
/// den Kasten wieder, damit nur die Nachricht im Verlauf bleibt.
/// Ohne echtes Terminal (umgeleitete Ein-/Ausgabe) gibt es nur ein schlichtes "› " mit ReadLine.
/// </summary>
internal sealed class InputBox(IAnsiConsole console, Func<DateTime> clock, bool fancy, LineEditor? editor = null)
{
    private const string Prompt = " › ";
    private const int PrefixWidth = 3;
    /// <summary>Höchstens so viele Textzeilen sind gleichzeitig zu sehen; darüber wird mitgescrollt.</summary>
    private const int MaxVisibleRows = 12;

    private readonly Lock _lock = new();
    private string? _hint;
    // Wie weit der Cursor unter dem oberen Rand des Kastens steht, und wie hoch der Kasten ist.
    private int _cursorRow;
    private int _height;
    private bool _drawn;

    /// <summary>Liest eine Eingabe (auch mehrzeilig). <c>null</c> bei Eingabe-Ende.</summary>
    public string? ReadLine()
    {
        if (!fancy || editor is null)
        {
            console.Markup($"[{Theme.Tag(Theme.Accent)}]›[/] ");
            return Console.ReadLine();
        }

        editor.Reset();
        lock (_lock)
        {
            _hint = null;
            _drawn = false;
            Draw();
        }

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            var pending = Console.KeyAvailable;
            var outcome = editor.Handle(key, pending);

            lock (_lock)
            {
                _hint = null;
                // Beim Einfügen erst zeichnen, wenn alles angekommen ist – sonst flackert es.
                if (!pending || outcome == LineEditor.Outcome.Submit)
                    Draw();
            }

            if (outcome == LineEditor.Outcome.Submit)
                return editor.Text;
        }
    }

    /// <summary>Entfernt den Kasten nach dem Senden. Danach steht der Cursor dort, wo der Kasten begann.</summary>
    public void Erase()
    {
        if (!fancy)
            return;
        lock (_lock)
        {
            if (!_drawn)
                return;
            MoveToTop();
            console.WriteAnsi("\u001b[J");
            _drawn = false;
        }
    }

    /// <summary>Zeigt einen Hinweis in der Statuszeile, während auf Eingabe gewartet wird.</summary>
    public void ShowHint(string hint)
    {
        if (!fancy || editor is null)
        {
            console.MarkupLine($"\n[{Theme.Tag(Theme.Muted)}]{Markup.Escape(hint)}[/]");
            return;
        }
        lock (_lock)
        {
            _hint = hint;
            Draw();
        }
    }

    /// <summary>Setzt den Cursor unter den Kasten, z. B. bevor Max sich verabschiedet.</summary>
    public void MoveBelow()
    {
        if (!fancy)
        {
            console.WriteLine();
            return;
        }
        lock (_lock)
        {
            if (_drawn && _height - _cursorRow > 0)
                console.Cursor.MoveDown(_height - _cursorRow);
            console.WriteAnsi("\r");
            console.WriteLine();
        }
    }

    private void MoveToTop()
    {
        if (_cursorRow > 0)
            console.Cursor.MoveUp(_cursorRow);
        console.WriteAnsi("\r");
    }

    /// <summary>Zeichnet den ganzen Kasten neu und setzt den Cursor an die Schreibstelle.</summary>
    private void Draw()
    {
        if (_drawn)
            MoveToTop();
        console.WriteAnsi("\u001b[J");

        // Eine Spalte Luft zum Rand – volle Zeilen brechen in manchen Terminals sofort um.
        var width = Math.Max(20, console.Profile.Width - 1);
        var layout = Layout(editor!.Text, editor.Cursor, width - PrefixWidth);

        var first = Math.Clamp(layout.CursorRow - MaxVisibleRows + 1, 0, Math.Max(0, layout.Rows.Count - MaxVisibleRows));
        var visible = layout.Rows.Skip(first).Take(MaxVisibleRows).ToList();

        var rule = new string('─', width);
        var border = new Style(Theme.Border);
        var text = new Style(Theme.Text);
        var muted = new Style(Theme.Muted);

        console.Write(rule, border);
        console.Write("\n");
        for (var i = 0; i < visible.Count; i++)
        {
            var row = visible[i];
            if (row.IsFirstOfText)
                console.Write(Prompt, new Style(Theme.Accent));
            else if (i == 0 && first > 0)
                console.Write(" … ", muted); // es gibt Zeilen darüber
            else
                console.Write("   ");
            console.Write(row.Text, text);
            console.Write("\n");
        }
        console.Write(rule, border);
        console.Write("\n");
        console.Write(new Padder(BuildStatus(layout.LogicalLines), new Padding(0, 0, 1, 0)));

        // Der Status endet mit einem Zeilenumbruch: Der Cursor steht jetzt unter dem Kasten.
        _height = visible.Count + 3;
        _drawn = true;
        var cursorRow = 1 + (layout.CursorRow - first);
        console.Cursor.MoveUp(_height - cursorRow);
        console.WriteAnsi("\r");
        var column = PrefixWidth + layout.CursorColumn;
        if (column > 0)
            console.WriteAnsi($"\u001b[{column}C");
        _cursorRow = cursorRow;
    }

    private Grid BuildStatus(int lines)
    {
        var muted = Theme.Tag(Theme.Muted);
        var hint = _hint ?? editor?.Hint;
        var left = hint is not null
            ? $" [{Theme.Tag(Theme.Accent)}]●[/] [{Theme.Tag(Theme.Text)}]{Markup.Escape(hint)}[/]"
            : lines > 1
                ? $" [{Theme.Tag(Theme.Success)}]●[/] [{muted}]{lines} Zeilen[/]"
                : $" [{Theme.Tag(Theme.Success)}]●[/] [{muted}]bereit[/]";
        var right = lines > 1
            ? "Enter senden · Shift+Enter neue Zeile"
            : "/help · Shift+Enter neue Zeile";

        var status = new Grid()
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().NoWrap().RightAligned());
        status.Expand = true;
        status.AddRow(
            new Markup(left),
            new Markup($"[{muted}]{right} · {clock().ToString("HH:mm", CultureInfo.InvariantCulture)}[/] "));
        return status;
    }

    internal sealed record Row(string Text, bool IsFirstOfText);

    internal sealed record BoxLayout(List<Row> Rows, int CursorRow, int CursorColumn, int LogicalLines);

    /// <summary>
    /// Verteilt den Text auf Bildschirmzeilen (lange Zeilen werden umgebrochen) und
    /// findet heraus, in welcher Zeile und Spalte der Cursor steht.
    /// </summary>
    internal static BoxLayout Layout(string text, int cursor, int width)
    {
        var rows = new List<Row>();
        int cursorRow = 0, cursorColumn = 0;
        var lines = text.Split('\n');
        var offset = 0;

        for (var l = 0; l < lines.Length; l++)
        {
            var line = lines[l];
            var row = new StringBuilder();
            var rowWidth = 0;
            var elements = StringInfo.GetTextElementEnumerator(line);
            var position = offset;

            void Place()
            {
                if (cursor == position)
                {
                    cursorRow = rows.Count;
                    cursorColumn = rowWidth;
                }
            }

            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var w = CellWidth.Of(element);
                if (rowWidth + w > width && row.Length > 0)
                {
                    rows.Add(new Row(row.ToString(), l == 0 && rows.Count == 0));
                    row.Clear();
                    rowWidth = 0;
                }
                Place();
                row.Append(element);
                rowWidth += w;
                position += element.Length;
            }
            Place(); // Cursor am Zeilenende
            rows.Add(new Row(row.ToString(), l == 0 && rows.Count == 0));
            offset += line.Length + 1;
        }

        return new BoxLayout(rows, cursorRow, cursorColumn, lines.Length);
    }
}
