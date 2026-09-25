namespace Max.Ui;

/// <summary>
/// Strg+C an der Eingabe: Einmal drücken zeigt einen Hinweis,
/// ein zweites Mal innerhalb kurzer Zeit beendet Max.
/// </summary>
internal sealed class CtrlCPolicy(TimeSpan window)
{
    private DateTime? _lastPress;

    public CtrlCPolicy() : this(TimeSpan.FromSeconds(2)) { }

    public enum Action { ShowHint, Exit }

    public Action Press(DateTime now)
    {
        var isSecond = WasPressedRecently(now);
        _lastPress = now;
        return isSecond ? Action.Exit : Action.ShowHint;
    }

    public bool WasPressedRecently(DateTime now) =>
        _lastPress is { } last && now - last <= window;
}
