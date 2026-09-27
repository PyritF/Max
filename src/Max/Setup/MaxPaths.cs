namespace Max.Setup;

/// <summary>
/// Wo Max seine Daten ablegt: Modell, Zustand, später Updates und Gedächtnis.
/// Windows: <c>%LOCALAPPDATA%\Max</c>, Linux: <c>$XDG_DATA_HOME/max</c> bzw. <c>~/.local/share/max</c>.
/// Mit der Umgebungsvariable <c>MAX_HOME</c> lässt sich der Ordner verlegen (Tests, Ausprobieren).
/// </summary>
internal sealed class MaxPaths(string root)
{
    public string Root { get; } = root;

    /// <summary>Das Modell. Bewusst neutral benannt – Max ist Max.</summary>
    public string Model => Path.Combine(Root, "core.bin");

    /// <summary>Unfertiger Download; bleibt liegen, damit es beim nächsten Start weitergeht.</summary>
    public string ModelPart => Path.Combine(Root, "core.bin.part");

    public string State => Path.Combine(Root, "state.json");

    /// <summary>Einstellungen, die man in Max selbst ändert (z. B. /denken).</summary>
    public string Settings => Path.Combine(Root, "settings.json");

    /// <summary>Hierhin lädt Max neue Versionen im Hintergrund.</summary>
    public string UpdateDir => Path.Combine(Root, "update");

    /// <summary>Ein neues Modell, fertig geladen und geprüft – wird beim nächsten Start zu <see cref="Model"/>.</summary>
    public string ModelNext => Path.Combine(Root, "core.next.bin");

    /// <summary>Der Zustand zu <see cref="ModelNext"/>.</summary>
    public string StateNext => Path.Combine(Root, "state.next.json");

    /// <summary>Frühere Eingaben für ↑/↓.</summary>
    public string History => Path.Combine(Root, "history.txt");

    /// <summary>Der fertig gerechnete System-Prompt – damit der nächste Start nicht wieder alles rechnen muss.</summary>
    public string PromptCache => Path.Combine(Root, "cache");

    /// <summary>Protokoll von llama.cpp – im Terminal hätte es nichts verloren.</summary>
    public string EngineLog => Path.Combine(Root, "logs", "llama.log");

    public void EnsureExists() => Directory.CreateDirectory(Root);

    public static MaxPaths Default() => new(ResolveRoot(
        Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    internal static string ResolveRoot(Func<string, string?> env, bool isWindows, string localAppData, string home)
    {
        var custom = env("MAX_HOME");
        if (!string.IsNullOrWhiteSpace(custom))
            return Path.GetFullPath(custom);

        if (isWindows)
            return Path.Combine(localAppData, "Max");

        var xdg = env("XDG_DATA_HOME");
        var dataHome = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg;
        return Path.Combine(dataHome, "max");
    }
}
