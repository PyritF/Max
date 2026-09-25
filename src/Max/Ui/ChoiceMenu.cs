using System.Globalization;
using System.Text;
using Spectre.Console;

namespace Max.Ui;

/// <summary>
/// Das Auswahlmenü für Max' Rückfragen – an der Stelle der Eingabe:
/// <code>
/// ──────────────────────────────
///  ❯ 1  Erste Antwort
///    2  Zweite Antwort
///    ✎  Eigene Antwort …
/// ──────────────────────────────
///  ● Rückfrage          ↑↓ wählen · Enter senden · Esc normale Eingabe
/// </code>
/// Die Tasten verarbeitet <see cref="ChoiceMenuState"/>; hier wird nur gezeichnet. Nach der Wahl
/// verschwindet das Menü wieder, die Antwort erscheint wie eine getippte Nachricht.
/// </summary>
internal sealed class ChoiceMenu(IAnsiConsole console, Func<DateTime> clock)
{
    private const string FreeTextLabel = "Eigene Antwort …";

    private int _cursorRow;
    private int _height;
    private bool _drawn;

    /// <summary>Zeigt das Menü und wartet auf eine Wahl. Null bei Esc – dann gilt die normale Eingabe.</summary>
    public string? Ask(ChoiceQuestion question)
    {
        var state = new ChoiceMenuState(question.Options);
        _drawn = false;
        Draw(state);
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                var pending = Console.KeyAvailable;
                var outcome = state.Handle(key, pending);
                if (!pending || outcome != ChoiceMenuState.Outcome.Continue)
                    Draw(state);

                if (outcome == ChoiceMenuState.Outcome.Submit)
                    return state.Answer;
                if (outcome == ChoiceMenuState.Outcome.Cancel)
                    return null;
            }
        }
        finally
        {
            Erase();
            console.Cursor.Show(true);
        }
    }

    private void Erase()
    {
        if (!_drawn)
            return;
        MoveToTop();
        console.WriteAnsi("\u001b[J");
        _drawn = false;
    }

    private void MoveToTop()
    {
        if (_cursorRow > 0)
            console.Cursor.MoveUp(_cursorRow);
        console.WriteAnsi("\r");
    }

    private void Draw(ChoiceMenuState state)
    {
        if (_drawn)
            MoveToTop();
        console.WriteAnsi("\u001b[J");

        var width = Math.Max(20, console.Profile.Width - 1);
        var rule = new string('─', width);
        var border = new Style(Theme.Border);
        var accent = new Style(Theme.Accent);
        var text = new Style(Theme.Text);
        var muted = new Style(Theme.Muted);

        console.Write(rule, border);
        console.Write("\n");

        for (var i = 0; i <= state.Options.Count; i++)
        {
            var selected = i == state.Selected;
            console.Write(selected ? " ❯ " : "   ", accent);

            if (i < state.Options.Count)
            {
                console.Write($"{i + 1}  ", selected ? accent : muted);
                console.Write(Fit(state.Options[i], width - 6), selected ? new Style(Theme.Text, decoration: Decoration.Bold) : text);
            }
            else
            {
                console.Write("✎  ", selected ? accent : muted);
                if (state.FreeText.Length > 0)
                    console.Write(FitEnd(state.FreeText, width - 7), text);
                else
                    console.Write(FreeTextLabel, new Style(Theme.Placeholder));
            }
            console.Write("\n");
        }

        console.Write(rule, border);
        console.Write("\n");
        console.Write(new Padder(BuildStatus(state), new Padding(0, 0, 1, 0)));

        _height = state.Options.Count + 4;
        _drawn = true;

        // Cursor nur in der Freitext-Zeile zeigen, am Ende des Texts.
        var row = 1 + state.Options.Count;
        console.Cursor.MoveUp(_height - row);
        console.WriteAnsi("\r");
        _cursorRow = row;
        if (state.OnFreeText)
        {
            var column = 6 + (state.FreeText.Length > 0 ? CellWidth.OfText(FitEnd(state.FreeText, width - 7)) : 0);
            console.WriteAnsi($"\u001b[{column}C");
            console.Cursor.Show(true);
        }
        else
        {
            console.Cursor.Show(false);
        }
    }

    private Grid BuildStatus(ChoiceMenuState state)
    {
        var muted = Theme.Tag(Theme.Muted);
        var keys = state.OnFreeText
            ? "↑↓ wählen · Enter senden · Esc normale Eingabe"
            : "↑↓ wählen · 1–9 direkt · Enter senden · Esc normale Eingabe";
        var status = new Grid()
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().NoWrap().RightAligned());
        status.Expand = true;
        status.AddRow(
            new Markup($" [{Theme.Tag(Theme.Accent)}]●[/] [{Theme.Tag(Theme.Text)}]Rückfrage[/]"),
            new Markup($"[{muted}]{keys} · {clock().ToString("HH:mm", CultureInfo.InvariantCulture)}[/] "));
        return status;
    }

    /// <summary>Kürzt hinten mit "…", damit eine Zeile nie umbricht.</summary>
    internal static string Fit(string text, int width)
    {
        text = text.ReplaceLineEndings(" ");
        if (CellWidth.OfText(text) <= width)
            return text;
        var result = new StringBuilder();
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var w = CellWidth.Of(element);
            if (used + w > width - 1)
                break;
            result.Append(element);
            used += w;
        }
        return result.Append('…').ToString();
    }

    /// <summary>Zeigt bei langem Freitext das Ende (dort wird getippt), vorne mit "…".</summary>
    internal static string FitEnd(string text, int width)
    {
        if (CellWidth.OfText(text) <= width)
            return text;
        var elements = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
            elements.Add(e.GetTextElement());
        var result = new List<string>();
        var used = 1;
        for (var i = elements.Count - 1; i >= 0; i--)
        {
            var w = CellWidth.Of(elements[i]);
            if (used + w > width)
                break;
            result.Insert(0, elements[i]);
            used += w;
        }
        return "…" + string.Concat(result);
    }
}
