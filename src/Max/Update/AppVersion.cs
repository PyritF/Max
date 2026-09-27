namespace Max.Update;

/// <summary>Welche Version läuft, und ist eine andere neuer?</summary>
internal static class AppVersion
{
    public static string Current => typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Welche Datei aus dem Release zu diesem System gehört (Schlüssel in <c>app.assets</c> des Manifests).</summary>
    public static string AssetKey => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    /// <summary>"0.10.0" ist neuer als "0.9.1"; Unlesbares ist nie neuer.</summary>
    public static bool IsNewer(string? candidate, string current) =>
        Parse(candidate) is { } c && Parse(current) is { } now && c > now;

    internal static Version? Parse(string? value) =>
        Version.TryParse(value?.Trim().TrimStart('v'), out var version) ? version : null;

    /// <summary>
    /// Pfad der laufenden Exe – nur, wenn Max als einzelne Datei läuft. Unter <c>dotnet run</c> (Entwicklung)
    /// gibt es nichts zu ersetzen; <c>MAX_NO_UPDATE=1</c> schaltet Updates ganz ab.
    /// </summary>
    public static string? ExecutablePath
    {
        get
        {
#pragma warning disable IL3000 // Leer heißt genau das, was hier gefragt ist: Max läuft als einzelne Datei.
            var singleFile = string.IsNullOrEmpty(typeof(AppVersion).Assembly.Location);
#pragma warning restore IL3000
            return singleFile && Environment.GetEnvironmentVariable("MAX_NO_UPDATE") != "1" ? Environment.ProcessPath : null;
        }
    }
}
