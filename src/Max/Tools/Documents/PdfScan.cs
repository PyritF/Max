using Max.Llm;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Filters;

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
        if (Jpeg(image) is { } jpeg)
            return (jpeg, ".jpg");
        return image.TryGetPng(out var png) ? (png, ".png") : null;
    }

    /// <summary>
    /// Die JPEG-Daten – auch wenn vor dem JPEG noch Filter stehen ([/ASCII85Decode /DCTDecode], [/FlateDecode
    /// /DCTDecode]): Die packt Max aus, das JPEG selbst liest dann der Bild-Zusatz. Null, wenn es kein JPEG ist.
    /// </summary>
    private static byte[]? Jpeg(IPdfImage image)
    {
        var raw = image.RawMemory;
        if (IsJpeg(raw.Span))
            return raw.ToArray();
        try
        {
            var filters = DefaultFilterProvider.Instance.GetFilters(image.ImageDictionary);
            if (filters.Count < 2 || filters[^1] is not DctDecodeFilter)
                return null;
            Memory<byte> data = raw.ToArray();
            for (var i = 0; i < filters.Count - 1; i++)
                data = filters[i].Decode(data, image.ImageDictionary, DefaultFilterProvider.Instance, i);
            return IsJpeg(data.Span) ? data.ToArray() : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsJpeg(ReadOnlySpan<byte> bytes) => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
}
