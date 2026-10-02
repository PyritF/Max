using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Max.Tools.Documents;

namespace Max.Tools;

/// <summary>
/// Findet Dateien auf diesem Rechner: erst nach dem Namen (schnell), dann, solange Zeit ist, nach dem Inhalt von
/// Text-, Office- und PDF-Dateien – die zuletzt geänderten zuerst. Gesucht wird im Benutzerordner oder in einem
/// angegebenen Ordner, die flachen Ordner vor den tiefen. Versteckte Ordner, Programmdaten, Papierkorb und
/// Schlüssel bleiben außen vor; Cloud-Dateien, die nur als Platzhalter da sind, werden nicht geöffnet (sonst würden
/// sie dabei heruntergeladen).
/// </summary>
/// <param name="home">Wo ohne Angabe gesucht wird – sonst der Benutzerordner.</param>
internal sealed partial class FindFilesTool(Func<string> workingDirectory, Func<string>? home = null) : ITool
{
    internal const int MaxHits = 15;
    internal static readonly TimeSpan NameTime = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan TotalTime = TimeSpan.FromSeconds(45);

    /// <summary>Höchstens so viele Einträge – ein Rechner kann Millionen Dateien haben.</summary>
    internal const int MaxEntries = 400_000;

    public string Name => "finden";
    public string? Argument => "Suchbegriffe, optional mit | Ordnerpfad";
    public string Description => "Findet Dateien auf diesem Rechner nach Name und Inhalt (Text, Word, Excel, PDF …) – im Benutzerordner oder im angegebenen Ordner.";
    public string Describe(string argument)
    {
        var (query, folder) = ReadFileTool.Split(argument);
        return folder.Length > 0 ? $"Suche Dateien: {query} (in {Folder(folder)})" : $"Suche Dateien: {query}";
    }

    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public Task<string> RunAsync(string argument, CancellationToken ct) => Task.Run(() => Run(argument, ct), ct);

    private string Run(string argument, CancellationToken ct)
    {
        var (query, folder) = ReadFileTool.Split(argument);
        var root = home?.Invoke() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (folder.Length > 0)
        {
            // "| Ordner: Downloads" – die Beschriftung schreibt das Modell gern mit. Gibt es den Ordner wörtlich, gilt er.
            var exact = ToolPaths.Resolve(folder, workingDirectory());
            root = Directory.Exists(exact) ? exact : ToolPaths.Resolve(Folder(folder), workingDirectory());
        }
        if (ToolPaths.Forbidden(root) is { } reason)
            return reason;
        if (!Directory.Exists(root))
            return $"Den Ordner {root} gibt es nicht.";

        var (terms, types) = Split(query);
        if (terms.Count == 0 && types.Count == 0)
            return $"Wonach soll ich suchen? „{query}“ enthält keine brauchbaren Suchbegriffe.";

        var clock = Stopwatch.StartNew();
        var byName = new List<(FileInfo File, int InName)>();
        var candidates = new List<FileInfo>();
        var (seen, complete) = Walk(root, file =>
        {
            if (types.Count > 0 && !types.Contains(file.Extension.ToLowerInvariant()))
                return;
            var path = DocumentSearch.Fold(Path.GetRelativePath(root, file.FullName));
            var name = DocumentSearch.Fold(file.Name);
            if (terms.All(t => DocumentSearch.Count(path, t) > 0))
                byName.Add((file, terms.Count(t => DocumentSearch.Count(name, t) > 0)));
            else if (terms.Count > 0 && Searchable(file))
                candidates.Add(file);
        }, ct, clock);

        // Im Inhalt: die zuletzt geänderten Dateien zuerst, solange Zeit ist.
        var byContent = new List<(FileInfo File, string Excerpt)>();
        var searched = 0;
        foreach (var file in candidates.OrderByDescending(f => f.LastWriteTimeUtc))
        {
            if (clock.Elapsed > TotalTime || byContent.Count >= MaxHits)
                break;
            ct.ThrowIfCancellationRequested();
            searched++;
            if (Excerpt(file, terms, ct) is { } excerpt)
                byContent.Add((file, excerpt));
        }

        return Report(query, root, byName, byContent, seen, complete && searched == candidates.Count, clock.Elapsed);
    }

    private static string Report(string query, string root, List<(FileInfo File, int InName)> byName, List<(FileInfo File, string Excerpt)> byContent,
        int seen, bool complete, TimeSpan elapsed)
    {
        var text = new StringBuilder($"Gesucht nach „{query}“ in {root} ({seen:N0} Dateien angesehen, {elapsed.TotalSeconds:0.0} s):\n".Replace(',', '.'));
        if (byName.Count == 0 && byContent.Count == 0)
        {
            text.Append("Nichts gefunden. Vielleicht andere Begriffe oder ein anderer Ordner (`finden: Begriffe | Ordner`).");
        }
        else
        {
            if (byName.Count > 0)
            {
                text.Append("Im Namen:\n");
                foreach (var (file, _) in byName.OrderByDescending(h => h.InName).ThenByDescending(h => h.File.LastWriteTimeUtc).Take(MaxHits))
                    text.Append("- ").Append(Line(file)).Append('\n');
                if (byName.Count > MaxHits)
                    text.Append($"… und {byName.Count - MaxHits} weitere\n");
            }
            if (byContent.Count > 0)
            {
                text.Append("Im Inhalt:\n");
                foreach (var (file, excerpt) in byContent)
                    text.Append("- ").Append(Line(file)).Append(": „").Append(excerpt).Append("“\n");
            }
            text.Append("(Lesen mit `datei: <Pfad>`.)");
        }
        if (!complete)
            text.Append("\n(Nicht alles durchsucht – die Zeit war um. Mit `| Ordner` geht es gezielter.)");
        return text.ToString();
    }

    private static string Line(FileInfo file) =>
        $"{file.FullName} ({ListFolderTool.Size(file.Length)}, geändert {file.LastWriteTime:dd.MM.yyyy})";

    /// <summary>"Ordner: Downloads", "im Ordner Downloads", "Pfad: ." → "Downloads" bzw. "." – ohne die Beschriftung.</summary>
    internal static string Folder(string folder)
    {
        var text = folder.Trim().Trim('"', '„', '“', '\'', '`').Trim();
        var stripped = FolderLabelRegex().Replace(text, "", 1).Trim().Trim('"', '„', '“', '\'', '`').Trim();
        return stripped.Length > 0 ? stripped : text;
    }

    [GeneratedRegex(@"^(?:(?:im|in|unter)\s+(?:dem\s+)?)?(?:ordner|verzeichnis|pfad|folder)\b\s*[:=]?\s*|^in\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex FolderLabelRegex();

    /// <summary>
    /// Die Begriffe der Suche – Dateitypen ("PDF", "Excel", "Fotos") werden zum Filter auf die Endung statt zum
    /// Namensteil: "Excel Umsatz" findet umsatz.xlsx.
    /// </summary>
    internal static (List<string> Terms, HashSet<string> Types) Split(string query)
    {
        var terms = new List<string>();
        var types = new HashSet<string>();
        foreach (var term in DocumentSearch.Terms(query))
        {
            if (TypeWords.TryGetValue(term, out var extensions))
                types.UnionWith(extensions);
            else if (term is not ("datei" or "dateien" or "ordner"))
                terms.Add(term);
        }
        return (terms, types);
    }

    private static readonly Dictionary<string, string[]> TypeWords = BuildTypeWords();

    private static Dictionary<string, string[]> BuildTypeWords()
    {
        var map = new Dictionary<string, string[]>();
        void Add(string[] words, params string[] extensions)
        {
            foreach (var word in words)
                map[DocumentSearch.Stem(DocumentSearch.Fold(word))] = extensions;
        }
        Add(["pdf", "pdfs"], ".pdf");
        Add(["excel", "tabelle", "tabellen"], ".xlsx", ".xlsm", ".xls", ".csv", ".ods");
        Add(["word", "worddokument"], ".docx", ".docm", ".doc", ".odt", ".rtf");
        Add(["powerpoint", "präsentation", "präsentationen", "folien"], ".pptx", ".pptm", ".ppt", ".odp", ".key");
        Add(["foto", "fotos", "bild", "bilder", "screenshot", "screenshots"], ".jpg", ".jpeg", ".png", ".heic", ".gif", ".webp", ".bmp");
        Add(["musik", "lied", "lieder", "song", "songs"], ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".opus");
        Add(["video", "videos", "film", "filme"], ".mp4", ".mov", ".mkv", ".avi", ".webm");
        Add(["aufnahme", "aufnahmen", "sprachnachricht", "sprachnachrichten", "audio"], ".mp3", ".m4a", ".wav", ".ogg", ".opus", ".flac", ".aac");
        return map;
    }

    /// <summary>Ordner, in denen fast nie etwas Gesuchtes liegt – aber sehr viele Dateien.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Application Data", "Local Settings", "node_modules", "bin", "obj", "Library", "$Recycle.Bin",
        "System Volume Information", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "__pycache__",
        "venv", "site-packages", "dist-packages", "snap", "Temp", "tmp",
    };

    /// <summary>
    /// Geht die Ordner der Breite nach durch (flache vor tiefen) und meldet jede Datei. Gibt zurück, wie viele es waren
    /// und ob es alle waren (nicht abgebrochen wegen Zeit oder Menge).
    /// </summary>
    internal static (int Seen, bool Complete) Walk(string root, Action<FileInfo> visit, CancellationToken ct, Stopwatch? clock = null)
    {
        clock ??= Stopwatch.StartNew();
        var queue = new Queue<DirectoryInfo>();
        queue.Enqueue(new DirectoryInfo(root));
        var seen = 0;
        while (queue.TryDequeue(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            if (clock.Elapsed > NameTime || seen >= MaxEntries)
                return (seen, false);
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = directory.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                continue;
            }
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Name.StartsWith('.') || (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    continue;
                if (ToolPaths.Forbidden(entry.FullName) is not null)
                    continue;
                if (entry is DirectoryInfo sub)
                {
                    // Verknüpfungen (Junctions, Symlinks) nicht verfolgen – sonst doppelt oder im Kreis.
                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0 && !SkippedFolders.Contains(sub.Name))
                        queue.Enqueue(sub);
                }
                else if (entry is FileInfo file)
                {
                    seen++;
                    visit(file);
                }
            }
        }
        return (seen, true);
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".log", ".json", ".xml", ".html", ".htm", ".ini", ".yaml", ".yml", ".toml", ".cs", ".py",
        ".js", ".ts", ".java", ".c", ".cpp", ".h", ".go", ".rs", ".sql", ".sh", ".ps1", ".bat", ".tex", ".rtf",
    };

    /// <summary>Lässt sich der Inhalt durchsuchen – und ist die Datei wirklich da (kein Cloud-Platzhalter)?</summary>
    internal static bool Searchable(FileInfo file)
    {
        const FileAttributes recallOnOpen = (FileAttributes)0x40000, recallOnDataAccess = (FileAttributes)0x400000;
        if ((file.Attributes & (FileAttributes.Offline | recallOnOpen | recallOnDataAccess)) != 0)
            return false;
        var extension = file.Extension.ToLowerInvariant();
        if (TextExtensions.Contains(extension))
            return file.Length <= 2 * 1024 * 1024;
        return DocumentReader.IsDocument(file.FullName) && file.Length <= 20 * 1024 * 1024;
    }

    /// <summary>Die erste Zeile mit einem der Begriffe – falls die Datei alle enthält, sonst null.</summary>
    private static string? Excerpt(FileInfo file, IReadOnlyList<string> terms, CancellationToken ct)
    {
        try
        {
            var lines = DocumentReader.Load(file.FullName, null, ct, maxPages: 10).Parts.SelectMany(p => p.Lines).ToList();
            var all = DocumentSearch.Fold(string.Join('\n', lines));
            if (!terms.All(t => DocumentSearch.Count(all, t) > 0))
                return null;
            var line = lines.FirstOrDefault(l => terms.Any(t => DocumentSearch.Count(DocumentSearch.Fold(l), t) > 0)) ?? "";
            line = line.Trim();
            return line.Length <= 160 ? line : line[..157] + "…";
        }
        catch (Exception e) when (e is DocumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
