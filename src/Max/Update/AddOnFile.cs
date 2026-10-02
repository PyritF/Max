using Max.Setup;
using Max.Ui;

namespace Max.Update;

/// <summary>
/// Ein Zusatz im Datenordner – der Bild-Zusatz (<c>vision.N.bin</c>) oder die Spracherkennung (<c>audio.N.bin</c>):
/// welcher liegt bereit, und laden (fortsetzbar, mit Prüfsumme). Beide kommen still im Hintergrund, nachdem das
/// Modell da ist; ohne sie geht alles andere wie gewohnt.
/// </summary>
internal sealed class AddOnFile(string prefix, string label)
{
    public static readonly AddOnFile Vision = new("vision", "Bildverständnis");
    public static readonly AddOnFile Audio = new("audio", "Spracherkennung");

    /// <summary>So heißt er für den Nutzer ("Bildverständnis").</summary>
    public string Label { get; } = label;

    public string PathFor(MaxPaths paths, int revision) => Path.Combine(paths.Root, $"{prefix}.{revision}.bin");

    /// <summary>Der neueste bereitliegende Zusatz – oder null. Geht auch offline, ohne Manifest.</summary>
    public string? Installed(MaxPaths paths)
    {
        if (!Directory.Exists(paths.Root))
            return null;
        return Directory.EnumerateFiles(paths.Root, $"{prefix}.*.bin")
            .Select(path => (Path: path, Revision: int.TryParse(Path.GetFileName(path).Split('.')[1], out var r) ? r : -1))
            .Where(f => f.Revision >= 0)
            .OrderByDescending(f => f.Revision)
            .Select(f => f.Path)
            .FirstOrDefault();
    }

    /// <summary>Fehlt der Zusatz aus dem Manifest noch?</summary>
    public bool IsMissing(MaxPaths paths, AddOnEntry? entry) => entry is not null && !File.Exists(PathFor(paths, entry.Revision));

    /// <summary>Lädt den Zusatz; ältere Revisionen werden danach gelöscht.</summary>
    public async Task<string> DownloadAsync(ModelDownloader downloader, MaxPaths paths, AddOnEntry entry, StepProgress progress, CancellationToken ct)
    {
        var target = PathFor(paths, entry.Revision);
        var part = target + ".part";
        await downloader.DownloadAsync(new DownloadTarget(entry.Url, entry.Sha256, entry.SizeBytes, part, paths.Root), progress, ct);
        File.Move(part, target, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(paths.Root, $"{prefix}.*.bin").Where(f => f != target))
        {
            try { File.Delete(old); } catch (IOException) { /* noch in Benutzung – beim nächsten Mal */ }
        }
        return target;
    }
}
