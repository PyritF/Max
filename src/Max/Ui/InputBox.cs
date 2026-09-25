using System.Globalization;
using Spectre.Console;

namespace Max.Ui;

/// <summary>
/// Die Eingabe unten: Linie, "› ", Linie und Statuszeile darunter.
/// Im echten Terminal springt der Cursor nach dem Zeichnen zurück in die Eingabezeile;
/// nach Enter wird der Kasten wieder entfernt, damit nur die Nachricht im Verlauf bleibt.
/// Ohne echtes Terminal (umgeleitete Ein-/Ausgabe) gibt es nur ein schlichtes "› ".
/// </summary>
internal sealed class InputBox(IAnsiConsole console, Func<DateTime> clock, bool fancy)
{
    // Zeilen vom oberen Rand des Kastens bis zur Eingabezeile bzw. bis unter die Statuszeile.
    private const int PromptRow = 1;
    private const int BoxHeight = 4;

    private readonly object _lock = new();

    /// <summary>Zeichnet den Kasten und liest eine Zeile. <c>null</c> bei Eingabe-Ende oder Strg+C (Windows).</summary>
    public string? ReadLine()
    {
        if (!fancy)
        {
            console.Markup($"[{Theme.Tag(Theme.Accent)}]›[/] ");
            return Console.ReadLine();
        }

        lock (_lock)
        {
            var line = NotFullWidth(new Rule().RuleStyle(new Style(Theme.Border)));
            console.Write(line);
            console.WriteLine();
            console.Write(line);
            console.Write(NotFullWidth(BuildStatus(null)));

            // Zurück in die (noch leere) Eingabezeile, hinter "› ".
            console.Cursor.MoveUp(BoxHeight - PromptRow);
            // "\r" als reines Steuerzeichen senden – im Markup würde Spectre daraus einen Zeilenumbruch machen.
            console.WriteAnsi("\r");
            console.Markup($" [{Theme.Tag(Theme.Accent)}]›[/] ");
        }

        return Console.ReadLine();
    }

    /// <summary>
    /// Entfernt den Kasten nach Enter. Der Cursor steht dann in der Zeile unter der Eingabe;
    /// von dort geht es an den oberen Rand und alles darunter wird gelöscht.
    /// </summary>
    public void Erase()
    {
        if (!fancy)
            return;

        lock (_lock)
        {
            console.Cursor.MoveUp(PromptRow + 1);
            console.WriteAnsi("\r\u001b[J");
        }
    }

    /// <summary>Zeigt einen Hinweis in der Statuszeile, während auf Eingabe gewartet wird.</summary>
    public void ShowHint(string hint)
    {
        if (!fancy)
        {
            console.MarkupLine($"\n[{Theme.Tag(Theme.Muted)}]{Markup.Escape(hint)}[/]");
            return;
        }

        lock (_lock)
        {
            // Cursor merken, zur Statuszeile springen, neu zeichnen, Cursor zurück.
            console.WriteAnsi("\u001b7");
            console.Cursor.MoveDown(BoxHeight - 1 - PromptRow);
            console.WriteAnsi("\r\u001b[2K");
            console.Write(NotFullWidth(BuildStatus(hint)));
            console.WriteAnsi("\u001b8");
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
            console.Cursor.MoveDown(BoxHeight - PromptRow);
            console.WriteAnsi("\r");
            console.WriteLine();
        }
    }

    /// <summary>
    /// Lässt die letzte Spalte frei. Eine Zeile, die exakt bis zum Rand reicht, bricht in manchen
    /// Terminals sofort um – dann stimmen die Cursor-Sprünge um eine Zeile nicht mehr.
    /// </summary>
    private static Padder NotFullWidth(Spectre.Console.Rendering.IRenderable content) =>
        new(content, new Padding(0, 0, 1, 0));

    private Grid BuildStatus(string? hint)
    {
        var muted = Theme.Tag(Theme.Muted);
        var left = hint is null
            ? $" [{Theme.Tag(Theme.Success)}]●[/] [{muted}]bereit[/]"
            : $" [{Theme.Tag(Theme.Accent)}]●[/] [{Theme.Tag(Theme.Text)}]{Markup.Escape(hint)}[/]";

        var status = new Grid()
            .AddColumn(new GridColumn().NoWrap())
            .AddColumn(new GridColumn().NoWrap().RightAligned());
        status.Expand = true;
        status.AddRow(
            new Markup(left),
            new Markup($"[{muted}]/help · Strg+C abbrechen · {clock().ToString("HH:mm", CultureInfo.InvariantCulture)}[/] "));
        return status;
    }
}
