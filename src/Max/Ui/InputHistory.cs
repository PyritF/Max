namespace Max.Ui;

/// <summary>
/// Frühere Eingaben für ↑/↓ – auch über Neustarts hinweg (Datei im Datenordner, bleibt lokal).
/// Beim Blättern wird der gerade angefangene Text gemerkt und am Ende wieder hergestellt.
/// </summary>
internal sealed class InputHistory(string? file, int limit = 500)
{
    private readonly List<string> _entries = Load(file, limit);
    private int _position = -1; // -1 = nicht am Blättern
    private string _draft = "";

    public IReadOnlyList<string> Entries => _entries;

    public void Add(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return;
        _entries.Remove(entry); // Wiederholungen nach hinten holen statt doppelt speichern
        _entries.Add(entry);
        if (_entries.Count > limit)
            _entries.RemoveRange(0, _entries.Count - limit);
        ResetNavigation();
        Save();
    }

    /// <summary>Einen Eintrag zurück. <paramref name="current"/> wird beim ersten Schritt als Entwurf gemerkt.</summary>
    public string? Previous(string current)
    {
        if (_entries.Count == 0)
            return null;
        if (_position == -1)
        {
            _draft = current;
            _position = _entries.Count;
        }
        if (_position == 0)
            return null;
        return _entries[--_position];
    }

    /// <summary>Einen Eintrag vor; nach dem neuesten kommt der Entwurf zurück.</summary>
    public string? Next()
    {
        if (_position == -1)
            return null;
        if (++_position >= _entries.Count)
        {
            _position = -1;
            return _draft;
        }
        return _entries[_position];
    }

    public void ResetNavigation()
    {
        _position = -1;
        _draft = "";
    }

    // Eine Eingabe pro Zeile; Zeilenumbrüche und Backslashes werden maskiert.
    internal static string Encode(string entry) => entry.Replace("\\", "\\\\").Replace("\n", "\\n");

    internal static string Decode(string line)
    {
        var result = new System.Text.StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length)
            {
                result.Append(line[++i] == 'n' ? '\n' : line[i]);
                continue;
            }
            result.Append(line[i]);
        }
        return result.ToString();
    }

    private void Save()
    {
        if (file is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllLines(file, _entries.Select(Encode));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static List<string> Load(string? file, int limit)
    {
        try
        {
            if (file is not null && File.Exists(file))
                return File.ReadAllLines(file).Select(Decode).Where(e => e.Length > 0).TakeLast(limit).ToList();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return [];
    }
}
