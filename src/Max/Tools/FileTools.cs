using System.Text;

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

/// <summary>Liest eine Textdatei. Binärdateien und sehr große Dateien nur mit Hinweis bzw. gekürzt.</summary>
internal sealed class ReadFileTool(Func<string> workingDirectory) : ITool
{
    /// <summary>Mehr wird nicht gelesen – die Toolbox kürzt ohnehin auf <see cref="ToolBox.MaxResultChars"/>.</summary>
    internal const int MaxBytes = 256 * 1024;

    public string Name => "datei";
    public string? Argument => "Pfad, z. B. README.md";
    public string Description => "Liest eine Textdatei (Code, Notizen, Konfiguration). Lange Dateien nur den Anfang.";
    public string Describe(string argument) => $"Lese {argument}";

    public async Task<string> RunAsync(string argument, CancellationToken ct)
    {
        var path = ToolPaths.Resolve(argument, workingDirectory());
        if (ToolPaths.Forbidden(path) is { } reason)
            return reason;
        if (Directory.Exists(path))
            return $"{path} ist ein Ordner – dafür gibt es \"ordner\".";
        if (!File.Exists(path))
            return $"Die Datei {path} gibt es nicht.";

        var length = new FileInfo(path).Length;
        var buffer = new byte[(int)Math.Min(length, MaxBytes)];
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), ct) is var n and > 0)
                read += n;
        }
        catch (UnauthorizedAccessException)
        {
            return $"Auf {path} habe ich keinen Zugriff.";
        }
        if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(buffer.Length, 8192)) >= 0)
            return $"{Path.GetFileName(path)} ist keine Textdatei ({ListFolderTool.Size(length)}).";

        var text = Encoding.UTF8.GetString(buffer).TrimStart('﻿');
        var header = $"Datei {path} ({ListFolderTool.Size(length)}):\n";
        return length > MaxBytes ? header + text + "\n… (nur der Anfang)" : header + text;
    }
}
