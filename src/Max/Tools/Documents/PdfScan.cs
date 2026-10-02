using Max.Llm;
using UglyToad.PdfPig;

namespace Max.Tools.Documents;

/// <summary>
/// Eingescannte PDF-Seiten: Eine solche Seite ist nur ein Bild. Max holt das größte Bild der Seite heraus (JPEG
/// unverändert, sonst als PNG) und lässt den Bild-Zusatz den Text abschreiben – Seite für Seite, nur auf Abruf.
/// </summary>
internal static class PdfScan
{
    internal const string Question =
        "Das ist eine eingescannte Dokumentseite. Schreib ihren gesamten Text wörtlich ab, Zeile für Zeile, in der " +
        "Sprache des Dokuments. Tabellen als Zeilen mit | zwischen den Spalten. Keine Beschreibung, nur der Text.";

    /// <summary>Eine ganze Seite Text braucht mehr als eine Bildbeschreibung.</summary>
    internal const int MaxTokens = 1200;

    /// <summary>Der Text der Seite – leer, wenn es kein lesbares Bild gibt; null, wenn das Bildverständnis (noch) fehlt.</summary>
    public static async Task<string?> ReadAsync(string path, int pageNumber, IVision? vision, CancellationToken ct)
    {
        if (vision is null)
            return null;
        var image = PageImage(path, pageNumber);
        if (image is null)
            return "";
        var file = Path.Combine(Path.GetTempPath(), $"max-scan-{Guid.NewGuid():N}{image.Value.Extension}");
        try
        {
            await File.WriteAllBytesAsync(file, image.Value.Bytes, ct);
            var text = await vision.LookAsync(file, Question, ct, MaxTokens);
            return text.Trim();
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Das größte Bild der Seite als Datei-Inhalt (JPEG oder PNG) – null bei Formaten, die nicht gehen (JBIG2, JPEG 2000).</summary>
    internal static (byte[] Bytes, string Extension)? PageImage(string path, int pageNumber)
    {
        using var pdf = PdfDocument.Open(path);
        if (pageNumber < 1 || pageNumber > pdf.NumberOfPages)
            return null;
        var image = pdf.GetPage(pageNumber).GetImages().MaxBy(i => i.BoundingBox.Width * i.BoundingBox.Height);
        if (image is null)
            return null;
        var raw = image.RawMemory.Span;
        if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8)
            return (raw.ToArray(), ".jpg");
        return image.TryGetPng(out var png) ? (png, ".png") : null;
    }
}
