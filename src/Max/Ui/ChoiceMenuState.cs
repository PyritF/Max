using System.Text;

namespace Max.Ui;

/// <summary>
/// Die Logik des Auswahlmenüs, ohne Terminal: welche Zeile markiert ist, was in der Freitext-Zeile steht
/// und was jede Taste bewirkt. Die letzte Zeile ist immer "Eigene Antwort" – wer einfach lostippt, landet dort.
/// </summary>
internal sealed class ChoiceMenuState(IReadOnlyList<string> options)
{
    public enum Outcome { Continue, Submit, Cancel }

    private readonly StringBuilder _free = new();

    public IReadOnlyList<string> Options { get; } = options;

    /// <summary>Markierte Zeile; <c>Options.Count</c> ist die Freitext-Zeile.</summary>
    public int Selected { get; private set; }

    public bool OnFreeText => Selected == Options.Count;

    public string FreeText => _free.ToString();

    /// <summary>Die gewählte Antwort, sobald <see cref="Outcome.Submit"/> kam.</summary>
    public string? Answer { get; private set; }

    public Outcome Handle(ConsoleKeyInfo key, bool moreKeysPending = false)
    {
        var rows = Options.Count + 1;

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                Selected = (Selected + rows - 1) % rows;
                return Outcome.Continue;
            case ConsoleKey.DownArrow:
            case ConsoleKey.Tab:
                Selected = (Selected + 1) % rows;
                return Outcome.Continue;
            case ConsoleKey.Escape:
                return Outcome.Cancel;
            case ConsoleKey.Backspace:
                if (OnFreeText && _free.Length > 0)
                    _free.Remove(_free.Length - 1, 1);
                return Outcome.Continue;
            case ConsoleKey.Enter:
                if (moreKeysPending && OnFreeText)
                {
                    _free.Append(' ');                     // eingefügter Text: Umbruch wird Leerzeichen
                    return Outcome.Continue;
                }
                if (!OnFreeText)
                {
                    Answer = Options[Selected];
                    return Outcome.Submit;
                }
                if (_free.ToString().Trim().Length == 0)
                    return Outcome.Continue;
                Answer = _free.ToString().Trim();
                return Outcome.Submit;
        }

        var c = key.KeyChar;

        // Ziffer auf einer Antwort-Zeile: Antwort direkt wählen. In der Freitext-Zeile ist sie einfach Text.
        if (c is >= '1' and <= '9' && !OnFreeText && !moreKeysPending && c - '1' < Options.Count)
        {
            Selected = c - '1';
            Answer = Options[Selected];
            return Outcome.Submit;
        }

        if (!char.IsControl(c))
        {
            Selected = Options.Count;
            _free.Append(c);
        }
        return Outcome.Continue;
    }
}
