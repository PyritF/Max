using System.Runtime.Intrinsics.X86;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Max.Llm;

/// <summary>Hört zu: Sprache → Text mit Zeitmarken.</summary>
internal interface IHearing
{
    /// <param name="samples">Mono, 16 kHz (siehe <see cref="Tools.AudioDecoder"/>).</param>
    Task<Transcript> TranscribeAsync(float[] samples, CancellationToken ct);
}

/// <summary>Was in einer Aufnahme gesagt wird.</summary>
/// <param name="Language">Erkannte Sprache ("de", "en" …) – null, wenn unklar.</param>
internal sealed record Transcript(string? Language, IReadOnlyList<TranscriptSegment> Segments);

internal sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Spracherkennung mit Whisper (whisper.cpp über Whisper.net, PLAN.md §9b). Das Modell kommt als Zusatz still im
/// Hintergrund (Manifest "audio") und wird erst bei der ersten Aufnahme geladen; danach bleibt es für die Sitzung.
/// Rechnet auf dem Prozessor – das Sprachmodell wartet währenddessen ohnehin.
/// </summary>
internal sealed class SpeechEngine(string modelPath) : IHearing, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    public async Task<Transcript> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _factory ??= await Task.Run(Load, ct);
            using var processor = _factory.CreateBuilder()
                .WithLanguage("auto")
                .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
                .Build();
            var segments = new List<TranscriptSegment>();
            string? language = null;
            await foreach (var segment in processor.ProcessAsync(samples, ct))
            {
                language ??= segment.Language;
                if (segment.Text.Trim() is { Length: > 0 } text)
                    segments.Add(new TranscriptSegment(segment.Start, segment.End, text));
            }
            return new Transcript(language, segments);
        }
        finally
        {
            _gate.Release();
        }
    }

    private WhisperFactory Load()
    {
        // Ohne AVX2 gibt es die Bibliothek nicht (die ältere Variante fehlt bewusst – sie wäre viel zu langsam).
        if (!Avx2.IsSupported && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            throw new PlatformNotSupportedException("Dieser Prozessor ist zu alt für die Spracherkennung (ohne AVX2).");
        Configure();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var factory = WhisperFactory.FromPath(modelPath);
        LlmEngine.Log($"Spracherkennung geladen in {clock.Elapsed.TotalSeconds:0.0} s.");
        return factory;
    }

    private static bool _configured;

    /// <summary>
    /// Als einzelne Datei entpackt .NET die Whisper-Bibliotheken in einen eigenen Ordner (runtimes/&lt;System&gt;/) –
    /// Whisper.net sucht dort, wo <see cref="RuntimeOptions.LibraryPath"/> liegt. Sein Protokoll geht ins Log.
    /// </summary>
    private static void Configure()
    {
        if (_configured)
            return;
        _configured = true;
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
        var rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "macos-x64";
        var folder = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(AppContext.BaseDirectory)
            .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "runtimes", rid)));
        if (folder is not null)
            RuntimeOptions.LibraryPath = Path.Combine(folder, "whisper");
        LogProvider.AddLogger((level, message) => LlmEngine.LogNative(message ?? ""));
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}
