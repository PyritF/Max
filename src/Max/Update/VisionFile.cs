using Max.Setup;
using Max.Ui;

namespace Max.Update;

/// <summary>Der Bild-Zusatz im Datenordner: welcher liegt bereit, und laden (fortsetzbar, mit Prüfsumme).</summary>
internal static class VisionFile
{
    /// <summary>Der neueste bereitliegende Zusatz – oder null. Geht auch offline, ohne Manifest.</summary>
    public static string? Installed(MaxPaths paths)
    {
        if (!Directory.Exists(paths.Root))
            return null;
        return Directory.EnumerateFiles(paths.Root, "vision.*.bin")
            .Select(path => (Path: path, Revision: int.TryParse(Path.GetFileName(path).Split('.')[1], out var r) ? r : -1))
            .Where(f => f.Revision >= 0)
            .OrderByDescending(f => f.Revision)
            .Select(f => f.Path)
            .FirstOrDefault();
    }

    /// <summary>Fehlt der Zusatz aus dem Manifest noch?</summary>
    public static bool IsMissing(MaxPaths paths, VisionEntry? entry) => entry is not null && !File.Exists(paths.Vision(entry.Revision));

    /// <summary>Lädt den Zusatz; ältere Revisionen werden danach gelöscht.</summary>
    public static async Task<string> DownloadAsync(ModelDownloader downloader, MaxPaths paths, VisionEntry entry, StepProgress progress, CancellationToken ct)
    {
        var target = paths.Vision(entry.Revision);
        var part = target + ".part";
        await downloader.DownloadAsync(new DownloadTarget(entry.Url, entry.Sha256, entry.SizeBytes, part, paths.Root), progress, ct);
        File.Move(part, target, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(paths.Root, "vision.*.bin").Where(f => f != target))
        {
            try { File.Delete(old); } catch (IOException) { /* noch in Benutzung – beim nächsten Mal */ }
        }
        return target;
    }
}
