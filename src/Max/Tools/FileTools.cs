using System.Text;
using Max.Llm;
using Max.Tools.Documents;

namespace Max.Tools;

/// <summary>
/// Pfade für die Datei-Werkzeuge: relativ zum Ordner, in dem Max gestartet wurde, "~" für den Benutzerordner.
/// Schlüssel und Zugangsdaten bleiben tabu – auch wenn alles lokal bleibt, haben sie in einem Gespräch nichts verloren.
/// </summary>
internal static class ToolPaths
{
    private static readonly string[] SecretFolders = [".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".password-store"];

    private static readonly string[] SecretFiles =
    [
        "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa", ".env", ".netrc", ".pgpass", ".git-credentials", "credentials",
        "credentials.json", "secrets.json", "login data", "cookies", "key4.db", "logins.json",
    ];

    private static readonly string[] SecretExtensions = [".pem", ".key", ".pfx", ".p12", ".kdbx", ".keystore", ".jks"];

    public static string Resolve(string path, string workingDirectory)
    {
        var trimmed = path.Trim().Trim('"', '\'', '`');
        if (trimmed is "~" || trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
            trimmed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), trimmed.Length > 2 ? trimmed[2..] : "");
        if (trimmed.Length == 0 || trimmed == ".")
            return workingDirectory;
        return Path.GetFullPath(trimmed, workingDirectory);
    }

    /// <summary>Ein Grund, warum der Pfad tabu ist – oder null.</summary>
    public static string? Forbidden(string fullPath)
    {
        var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => SecretFolders.Contains(p, StringComparer.OrdinalIgnoreCase)))
            return "Dieser Ordner enthält Schlüssel oder Zugangsdaten – da schaue ich nicht hinein.";
        var name = Path.GetFileName(fullPath);
        if (SecretFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
            || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || SecretExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            return "Das sieht nach Schlüsseln oder Zugangsdaten aus – die lese ich nicht.";
        return null;
    }
}

/// <summary>Zeigt, was in einem Ordner liegt: Unterordner zuerst, dann Dateien mit Größe.</summary>
internal sealed class ListFolderTool(Func<string> workingDirectory) : ITool
{
    internal const int MaxEntries = 200;

    public string Name => "ordner";
    public string? Argument => "Pfad, z. B. . oder ~/Dokumente";
    public string Description => "Listet Unterordner und Dateien eines Ordners auf (\".\" ist der Ordner, in dem ich gestartet wurde).";
    public string Describe(string argument) => $"Sehe in den Ordner {argument}";

    public Task<string> RunAsync(string argument, CancellationToken ct)
    {
        var path = ToolPaths.Resolve(argument, workingDirectory());
        if (ToolPaths.Forbidden(path) is { } reason)
            return Task.FromResult(reason);
        if (File.Exists(path))
            return Task.FromResult($"{path} ist eine Datei, kein Ordner.");
        if (!Directory.Exists(path))
            return Task.FromResult($"Den Ordner {path} gibt es nicht.");

        var directory = new DirectoryInfo(path);
        var text = new StringBuilder($"Ordner {directory.FullName}:\n");
        var count = 0;
        try
        {
            foreach (var sub in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (++count > MaxEntries)
                    break;
                text.Append("- ").Append(sub.Name).Append("/\n");
            }
            foreach (var file in directory.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (++count > MaxEntries)
                    break;
                text.Append("- ").Append(file.Name).Append(" (").Append(Size(file.Length)).Append(")\n");
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult($"Auf {path} habe ich keinen Zugriff.");
        }
        if (count == 0)
            text.Append("(leer)");
        else if (count > MaxEntries)
            text.Append($"… und weitere (nur die ersten {MaxEntries} gezeigt)");
        return Task.FromResult(text.ToString().TrimEnd());
    }

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB",
    };
}

/// <summary>
/// Liest Dateien: Text und Code, PDF, Word, Excel, PowerPoint. Lange Dateien gezielt – mit " | Suchbegriff" die
/// passenden Stellen, mit " | Seite 7" (Zeile, Folie, Blatt) eine bestimmte Stelle (siehe <see cref="DocumentView"/>).
/// Gelesene Dateien bleiben kurz im Speicher, damit Blättern und Suchen nicht jedes Mal neu liest.
/// </summary>
internal sealed class ReadFileTool(Func<string> workingDirectory, Func<IVision?>? vision = null) : ITool
{
    private readonly RecentCache<Document> _recent = new();

    public string Name => "datei";
    public string? Argument => "Pfad, optional mit | Suchbegriff oder | Seite 7";
    public string Description =>
        "Liest eine Datei: Text und Code, PDF, Word, Excel, PowerPoint. Bei langen Dateien den Anfang – mit „| Suchbegriff“ " +
        "die passenden Stellen, mit „| Seite 7“ (bzw. Zeile, Folie, Blatt) eine bestimmte.";
    public string Describe(string argument) => Describe("Lese", argument);

    // Eingescannte PDF-Seiten liest der Bild-Zusatz – das dauert auf der CPU.
    // Eingescannte Seiten liest der Bild-Zusatz ab – auf der CPU bis zu einigen Minuten je Seite (Selbsttest 54: 309 s).
    public TimeSpan Timeout => TimeSpan.FromMinutes(10);

    public async Task<string> RunAsync(string argument, CancellationToken ct)
    {
        var (raw, selector) = Split(argument);
        var path = ToolPaths.Resolve(raw, workingDirectory());
        if (ToolPaths.Forbidden(path) is { } reason)
            return reason;
        if (Directory.Exists(path))
            return $"{path} ist ein Ordner – dafür gibt es \"ordner\".";
        if (!File.Exists(path))
            return $"Die Datei {path} gibt es nicht.";
        if (ImageTool.IsImage(path))
            return $"{Path.GetFileName(path)} ist ein Bild – dafür gibt es \"bild\".";
        if (AudioDecoder.IsAudio(path))
            return $"{Path.GetFileName(path)} ist eine Aufnahme – dafür gibt es \"audio\".";

        var info = new FileInfo(path);
        Document document;
        try
        {
            var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            document = await Task.Run(() => _recent.Get(key, () => DocumentReader.Load(path, vision, ct)), ct);
        }
        catch (DocumentException e)
        {
            return e.Message;
        }
        catch (UnauthorizedAccessException)
        {
            return $"Auf {path} habe ich keinen Zugriff.";
        }
        catch (IOException e)
        {
            return $"{Path.GetFileName(path)} ließ sich nicht lesen ({e.Message}).";
        }
        return await DocumentView.RenderAsync(document, $"Datei {path} ({ListFolderTool.Size(info.Length)})", $"{Name}: {raw}", selector, ct);
    }

    /// <summary>"vertrag.pdf | Kündigung" → Pfad und was darin gesucht wird.</summary>
    internal static (string Target, string Selector) Split(string argument)
    {
        var bar = argument.IndexOf('|');
        return bar < 0 ? (argument.Trim(), "") : (argument[..bar].Trim(), argument[(bar + 1)..].Trim());
    }

    /// <summary>"Lese vertrag.pdf", "Lese vertrag.pdf, Seite 7", "Suche in vertrag.pdf: Kündigung".</summary>
    internal static string Describe(string verb, string argument)
    {
        var (target, selector) = Split(argument);
        var name = Path.GetFileName(target.TrimEnd('/', '\\'));
        if (name.Length == 0 || target.Contains("://", StringComparison.Ordinal))
            name = target;
        if (selector.Length == 0)
            return $"{verb} {name}";
        // Eine ganze Frage (hineingezogene Datei mit Frage dazu) zeigt die Anzeige nicht noch einmal.
        if (DocumentView.Parse(selector) is DocumentView.Selection.Search)
            return selector.Length <= 40 && !selector.Contains('?') && selector.Split(' ').Length <= 4 ? $"Suche in {name}: {selector}" : $"{verb} {name}";
        return $"{verb} {name}, {selector}";
    }
}

/// <summary>Die zuletzt gelesenen Dinge (Dateien, Webseiten) – blättern und suchen liest nicht jedes Mal neu.</summary>
/// <param name="lifetime">So lange gilt ein Eintrag höchstens (Webseiten ändern sich) – null = bis er verdrängt wird.</param>
internal sealed class RecentCache<T>(int size = 4, TimeSpan? lifetime = null) where T : class
{
    private readonly LinkedList<(string Key, DateTime Added, T Value)> _entries = new();
    private readonly object _lock = new();

    /// <summary>Der gemerkte Wert – sonst wird er geladen (scheitert das Laden, wird nichts gemerkt).</summary>
    public T Get(string key, Func<T> load) => TryGet(key) ?? Add(key, load());

    public async Task<T> GetAsync(string key, Func<Task<T>> load) => TryGet(key) ?? Add(key, await load());

    private T? TryGet(string key)
    {
        lock (_lock)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.Key != key)
                    continue;
                _entries.Remove(node);
                if (lifetime is { } life && DateTime.UtcNow - node.Value.Added > life)
                    return null;
                _entries.AddFirst(node);
                return node.Value.Value;
            }
        }
        return null;
    }

    private T Add(string key, T value)
    {
        lock (_lock)
        {
            _entries.AddFirst((key, DateTime.UtcNow, value));
            while (_entries.Count > size)
                _entries.RemoveLast();
        }
        return value;
    }
}
