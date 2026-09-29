using Max.Llm;

namespace Max.Tools;

/// <summary>
/// Sieht sich ein Bild oder einen Screenshot an (<see cref="IVision"/>). Angabe: der Pfad, optional mit
/// " | Frage" dahinter – sonst beschreibt Max das Bild und schreibt sichtbaren Text ab.
/// </summary>
/// <param name="vision">Das Bildverständnis – null, solange der Bild-Zusatz noch nicht geladen ist.</param>
internal sealed class ImageTool(Func<IVision?> vision, Func<string> workingDirectory) : ITool
{
    /// <summary>Was llama.cpp lesen kann (stb_image).</summary>
    internal static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    internal const long MaxBytes = 25L * 1024 * 1024;

    internal const string DefaultQuestion =
        "Beschreibe das Bild genau und sachlich. Schreib sichtbaren Text wörtlich ab. Antworte auf Deutsch.";

    public string Name => "bild";
    public string? Argument => "Pfad zum Bild, optional mit | Frage dazu";
    public string Description => "Sieht sich ein Bild oder einen Screenshot an (png, jpg, bmp, gif): beschreibt es, liest Text ab oder beantwortet die Frage dazu.";
    public string Describe(string argument) => $"Sehe mir {Path.GetFileName(Split(argument).Path)} an";

    // Auf der CPU dauert ein Bild gut eine Minute oder länger.
    public TimeSpan Timeout => TimeSpan.FromMinutes(5);

    public async Task<string> RunAsync(string argument, CancellationToken ct)
    {
        var (raw, question) = Split(argument);
        var path = ToolPaths.Resolve(raw, workingDirectory());
        if (ToolPaths.Forbidden(path) is { } reason)
            return reason;
        if (!File.Exists(path))
            return $"Das Bild {path} gibt es nicht.";
        if (!IsImage(path))
            return $"{Path.GetFileName(path)} ist kein Bild, das ich lesen kann (nur {string.Join(", ", Extensions)}).";
        if (new FileInfo(path).Length > MaxBytes)
            return $"{Path.GetFileName(path)} ist zu groß (höchstens {MaxBytes / 1024 / 1024} MB).";
        if (vision() is not { } eyes)
            return "Das Bildverständnis ist noch nicht auf diesem Rechner – es wird im Hintergrund geladen. Später noch einmal versuchen.";

        var answer = await eyes.LookAsync(path, question.Length > 0 ? question + " Antworte auf Deutsch." : DefaultQuestion, ct);
        return answer.Length == 0 ? $"Zu {Path.GetFileName(path)} kam keine Beschreibung heraus." : $"Bild {path}:\n{answer}";
    }

    public static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>"foto.png | Was steht auf dem Schild?" → Pfad und Frage.</summary>
    internal static (string Path, string Question) Split(string argument)
    {
        var bar = argument.IndexOf('|');
        return bar < 0 ? (argument.Trim(), "") : (argument[..bar].Trim(), argument[(bar + 1)..].Trim());
    }
}
