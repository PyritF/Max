using System.Globalization;

namespace Max.Ui;

/// <summary>Formatierung von Zahlen für die Anzeige – deutsch, knapp.</summary>
internal static class Format
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Bytes als GB mit einer Nachkommastelle, z. B. "3,4".</summary>
    public static string Gigabytes(long bytes) =>
        (bytes / 1_000_000_000d).ToString("0.0", German);

    /// <summary>Speichergröße (RAM) in ganzen GB, z. B. "32 GB".</summary>
    public static string Memory(long bytes) =>
        $"{Math.Round(bytes / 1024d / 1024 / 1024)} GB";

    /// <summary>Geschwindigkeit, z. B. "38,2 MB/s".</summary>
    public static string Speed(double bytesPerSecond) =>
        (bytesPerSecond / 1_000_000d).ToString("0.0", German) + " MB/s";

    /// <summary>Restzeit als m:ss bzw. h:mm:ss, z. B. "1:52".</summary>
    public static string Duration(TimeSpan duration)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0, duration.TotalSeconds));
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>
    /// Kürzt lange Pfade in der Mitte: "C:\Users\…\Projekte\Max".
    /// Anfang und Ende sind meist aussagekräftiger als die Mitte.
    /// </summary>
    public static string ShortenPath(string path, int maxLength)
    {
        if (path.Length <= maxLength)
            return path;

        var keep = maxLength - 1; // 1 Zeichen für "…"
        var head = keep / 3;
        var tail = keep - head;
        return path[..head] + "…" + path[^tail..];
    }
}
