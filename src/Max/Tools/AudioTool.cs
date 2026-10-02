using System.Globalization;
using Max.Llm;
using Max.Tools.Documents;

namespace Max.Tools;

/// <summary>
/// Hört sich eine Aufnahme an (Sprachnachricht, Memo, Mitschnitt) und schreibt ab, was gesagt wird – Zeile für Zeile
/// mit Zeitmarke. Die Abschrift ist ein Dokument wie jedes andere: lange Aufnahmen zeigen erst den Anfang, mit
/// " | Suchbegriff" die passenden Stellen. Einmal abgeschrieben, bleibt sie kurz im Speicher.
/// </summary>
/// <param name="hearing">Die Spracherkennung – null, solange sie noch nicht geladen ist.</param>
internal sealed class AudioTool(Func<IHearing?> hearing, Func<string> workingDirectory) : ITool
{
    /// <summary>Länger hört Max nicht zu – auf dem Prozessor dauert schon das ein paar Minuten.</summary>
    internal const int MaxMinutes = 30;

    internal const long MaxBytes = 500L * 1024 * 1024;

    private readonly RecentCache<Heard> _recent = new();

    private sealed record Heard(Document Document, string Title);

    public string Name => "audio";
    public string? Argument => "Pfad zur Aufnahme, optional mit | Suchbegriff";
    public string Description =>
        "Hört sich eine Aufnahme an (Sprachnachricht, Memo, Mitschnitt – mp3, m4a, wav, ogg/opus …) und schreibt ab, was gesagt wird, mit Zeitmarken.";
    public string Describe(string argument) => $"Höre mir {Path.GetFileName(ReadFileTool.Split(argument).Target)} an";

    // Auf dem Prozessor braucht eine lange Aufnahme ihre Zeit.
    public TimeSpan Timeout => TimeSpan.FromMinutes(20);

    public async Task<string> RunAsync(string argument, CancellationToken ct)
    {
        var (raw, selector) = ReadFileTool.Split(argument);
        var path = ToolPaths.Resolve(raw, workingDirectory());
        if (ToolPaths.Forbidden(path) is { } reason)
            return reason;
        if (!File.Exists(path))
            return $"Die Aufnahme {path} gibt es nicht.";
        if (!AudioDecoder.IsAudio(path))
            return $"{Path.GetFileName(path)} ist keine Aufnahme, die ich kenne ({string.Join(", ", AudioDecoder.Extensions)}).";
        var info = new FileInfo(path);
        if (info.Length > MaxBytes)
            return $"{Path.GetFileName(path)} ist zu groß ({ListFolderTool.Size(info.Length)}).";
        if (hearing() is not { } ears)
            return "Die Spracherkennung ist noch nicht auf diesem Rechner – sie wird im Hintergrund geladen. Später noch einmal versuchen.";

        Heard heard;
        try
        {
            heard = await _recent.GetAsync($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", () => ListenAsync(path, info, ears, ct));
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or PlatformNotSupportedException or IOException or EndOfStreamException)
        {
            return $"{Path.GetFileName(path)} ließ sich nicht abspielen ({e.Message}).";
        }
        return await DocumentView.RenderAsync(heard.Document, heard.Title, $"{Name}: {raw}", selector, ct);
    }

    private static async Task<Heard> ListenAsync(string path, FileInfo info, IHearing ears, CancellationToken ct)
    {
        var (samples, cut) = await Task.Run(() => AudioDecoder.Load(path, MaxMinutes * 60, ct), ct);
        if (samples.Length < AudioDecoder.SampleRate / 4)
            throw new InvalidDataException("kein Ton darin");
        var transcript = await ears.TranscribeAsync(samples, ct);
        var length = TimeSpan.FromSeconds(samples.Length / (double)AudioDecoder.SampleRate);
        var lines = transcript.Segments.Count == 0
            ? ["(nichts Verständliches gesagt – nur Geräusche oder Stille)"]
            : transcript.Segments.Select(s => $"[{Clock(s.Start)}] {s.Text}").ToList();
        var language = transcript.Language is { Length: > 0 } code ? $", Sprache: {LanguageName(code)}" : "";
        var title = $"Aufnahme {path} ({Clock(length)} min{(cut ? $", nur die ersten {MaxMinutes} Minuten" : "")}{language}, {ListFolderTool.Size(info.Length)})";
        return new Heard(new Document("Abschrift", "Zeile", lines.Count, [new DocumentPart(1, "", lines, Enumerable.Range(1, lines.Count).ToArray())]), title);
    }

    /// <summary>"1:05" bzw. "1:02:05".</summary>
    internal static string Clock(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private static string LanguageName(string code) => code switch
    {
        "de" => "Deutsch",
        "en" => "Englisch",
        "fr" => "Französisch",
        "es" => "Spanisch",
        "it" => "Italienisch",
        "nl" => "Niederländisch",
        "pl" => "Polnisch",
        "pt" => "Portugiesisch",
        "tr" => "Türkisch",
        "ru" => "Russisch",
        "uk" => "Ukrainisch",
        "hr" => "Kroatisch",
        "sr" => "Serbisch",
        "bs" => "Bosnisch",
        "cs" => "Tschechisch",
        "hu" => "Ungarisch",
        "ro" => "Rumänisch",
        "ar" => "Arabisch",
        "zh" => "Chinesisch",
        "ja" => "Japanisch",
        _ => code,
    };

    public static bool IsAudio(string path) => AudioDecoder.IsAudio(path);
}
