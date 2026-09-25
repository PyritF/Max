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

    public string UpdateDir => Path.Combine(Root, "update");

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
