using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Max.Setup;

namespace Max.Llm;

/// <summary>Was beim Laden herauskam – für /debug.</summary>
internal sealed record EngineInfo(
    string Description,
    string Architecture,
    string Backend,
    int GpuLayers,
    int LayerCount,
    int ContextSize,
    long ModelBytes,
    TimeSpan LoadTime);

/// <summary>Messwerte der letzten Antwort – für /debug.</summary>
internal sealed record GenerationStats(int PromptTokens, int ReusedTokens, int GeneratedTokens, TimeSpan TimeToFirstToken, double TokensPerSecond);

/// <summary>
/// Das lokale Sprachmodell (llama.cpp über LLamaSharp).
/// Hält einen einzigen Kontext für die ganze Sitzung und merkt sich, welche Tokens darin schon
/// verarbeitet sind: Beginnt der nächste Prompt genauso, wird nur der neue Teil gerechnet.
/// </summary>
internal sealed partial class LlmEngine : ILanguageModel, IDisposable
{
    private static readonly LLamaSeqId Sequence = LLamaSeqId.Zero;

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly List<int> _cached = [];
    private readonly SemaphoreSlim _busy = new(1, 1);

    private LlmEngine(LLamaWeights weights, LLamaContext context, EngineInfo info)
    {
        _weights = weights;
        _context = context;
        Info = info;
    }

    public EngineInfo Info { get; }
    public GenerationStats? LastRun { get; private set; }
    public int ContextSize => Info.ContextSize;
    public int CachedTokens => _cached.Count;

    /// <summary>
    /// Lädt das Modell. Mit brauchbarer Grafikkarte über Vulkan, sonst auf der CPU.
    /// Scheitert es auf der Grafikkarte (z. B. zu wenig Speicher), gibt es einen zweiten Versuch auf der CPU.
    /// </summary>
    public static async Task<LlmEngine> LoadAsync(string modelPath, int contextSize, HardwareInfo hardware, string logFile, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var useGpu = hardware.Gpu is not null;
        NativeSetup.Configure(useGpu, logFile);

        var gguf = GgufInfo.Read(modelPath);
        var modelBytes = new FileInfo(modelPath).Length;
        var layers = useGpu ? GpuOffload.Layers(modelBytes, gguf.BlockCount, hardware.VramBytes) : 0;
        if (gguf.TrainedContext > 0)
            contextSize = Math.Min(contextSize, gguf.TrainedContext);

        var parameters = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            GpuLayerCount = layers,
            BatchSize = 512,
        };

        LLamaWeights weights;
        try
        {
            weights = await LLamaWeights.LoadFromFileAsync(parameters, ct);
        }
        catch (Exception e) when (layers > 0 && e is not OperationCanceledException)
        {
            NativeSetup.Log($"GPU-Laden gescheitert, versuche CPU: {e.Message}");
            parameters.GpuLayerCount = layers = 0;
            weights = await LLamaWeights.LoadFromFileAsync(parameters, ct);
        }

        try
        {
            var context = await Task.Run(() => weights.CreateContext(parameters), ct);
            var offloaded = NativeSetup.OffloadedLayers ?? (layers > 0 ? Math.Min(layers, gguf.BlockCount) : 0);
            var info = new EngineInfo(
                Description: weights.NativeHandle.Description,
                Architecture: gguf.Architecture,
                Backend: offloaded > 0 ? "Vulkan" : "CPU",
                GpuLayers: offloaded,
                LayerCount: gguf.BlockCount,
                ContextSize: (int)context.ContextSize,
                ModelBytes: modelBytes,
                LoadTime: clock.Elapsed);
            return new LlmEngine(weights, context, info);
        }
        catch
        {
            weights.Dispose();
            throw;
        }
    }

    /// <summary>Schreibt eine eigene Zeile ins Protokoll (logs/llama.log).</summary>
    public static void Log(string message) => NativeSetup.Log(message);

    public IReadOnlyList<int> Tokenize(string text) =>
        Array.ConvertAll(_context.NativeHandle.Tokenize(text, add_bos: false, special: true, _context.Encoding), t => (int)t);

    public async IAsyncEnumerable<GeneratedPiece> GenerateAsync(
        IReadOnlyList<int> prompt, SamplingSettings sampling, [EnumeratorCancellation] CancellationToken ct)
    {
        if (prompt.Count == 0)
            yield break;
        if (prompt.Count >= ContextSize)
            throw new InvalidOperationException($"Prompt ({prompt.Count} Tokens) passt nicht in den Kontext ({ContextSize}).");

        await _busy.WaitAsync(ct);
        try
        {
            var clock = Stopwatch.StartNew();
            var reused = await PrepareAsync(prompt, ct);
            var timeToFirst = TimeSpan.Zero;
            var generated = 0;

            using var sampler = new DefaultSamplingPipeline
            {
                Temperature = sampling.Temperature,
                TopP = sampling.TopP,
                TopK = sampling.TopK,
                MinP = sampling.MinP,
                RepeatPenalty = sampling.RepeatPenalty,
            };
            var decoder = new StreamingTokenDecoder(_context);
            var vocab = _context.NativeHandle.Vocab;
            var batch = new LLamaBatch();
            var logitIndex = _lastLogitIndex;
            var genClock = new Stopwatch();

            try
            {
                while (generated < sampling.MaxTokens && _cached.Count < ContextSize)
                {
                    ct.ThrowIfCancellationRequested();

                    var token = sampler.Sample(_context.NativeHandle, logitIndex);
                    if (token.IsEndOfGeneration(vocab))
                        break;

                    decoder.Add(token);
                    var text = decoder.Read();

                    // Erst verarbeiten, dann ausgeben: So passt der Cache immer genau zu dem, was der Nutzer gesehen hat.
                    batch.Clear();
                    batch.Add(token, new LLamaPos { Value = _cached.Count }, Sequence, true);
                    await DecodeAsync(batch, ct);
                    _cached.Add((int)token);
                    logitIndex = 0;

                    if (generated++ == 0)
                    {
                        timeToFirst = clock.Elapsed;
                        genClock.Start();
                    }
                    yield return new GeneratedPiece((int)token, text);
                }
            }
            finally
            {
                var seconds = genClock.Elapsed.TotalSeconds;
                LastRun = new GenerationStats(prompt.Count, reused, generated, timeToFirst, seconds > 0 ? (generated - 1) / seconds : 0);
            }
        }
        finally
        {
            _busy.Release();
        }
    }

    private int _lastLogitIndex;

    /// <summary>
    /// Bringt den Kontext auf den Stand des Prompts. Liefert, wie viele Tokens wiederverwendet wurden.
    /// Weicht der Prompt vom Cache ab, wird der Kontext komplett neu gerechnet: Neuere Modelle
    /// (z. B. mit rekurrenten Schichten) lassen sich nicht zuverlässig auf eine Zwischenposition zurücksetzen.
    /// </summary>
    private async Task<int> PrepareAsync(IReadOnlyList<int> prompt, CancellationToken ct)
    {
        var common = 0;
        while (common < _cached.Count && common < prompt.Count && _cached[common] == prompt[common])
            common++;

        // Nur anhängen, wenn der ganze Cache passt und mindestens ein neues Token bleibt (für die Logits).
        if (common < _cached.Count || common == prompt.Count)
        {
            _context.NativeHandle.MemoryClear(true);
            _cached.Clear();
            common = 0;
        }

        var pending = prompt.Skip(common).Select(t => (LLamaToken)t).ToArray();
        var batch = new LLamaBatch();
        try
        {
            for (var start = 0; start < pending.Length; start += (int)_context.BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var length = Math.Min((int)_context.BatchSize, pending.Length - start);
                var last = start + length == pending.Length;
                batch.Clear();
                batch.AddRange(pending.AsSpan(start, length), new LLamaPos { Value = _cached.Count }, Sequence, logitsLast: last);
                await DecodeAsync(batch, ct);
                for (var i = start; i < start + length; i++)
                    _cached.Add((int)pending[i]);
                if (last)
                    _lastLogitIndex = batch.TokenCount - 1;
            }
        }
        catch
        {
            // Halb verarbeiteter Prompt: lieber sauber von vorn beim nächsten Mal.
            _context.NativeHandle.MemoryClear(true);
            _cached.Clear();
            throw;
        }

        return common;
    }

    private async Task DecodeAsync(LLamaBatch batch, CancellationToken ct)
    {
        // Rechnen auf einem Hintergrund-Thread, damit Spinner und Strg+C flüssig bleiben.
        var result = await Task.Run(() => _context.NativeHandle.Decode(batch), ct);
        if (result != DecodeResult.Ok)
            throw new InvalidOperationException($"llama.cpp: decode fehlgeschlagen ({result}).");
    }

    public void Dispose()
    {
        _context.Dispose();
        _weights.Dispose();
        _busy.Dispose();
    }

    /// <summary>Einmalige Einrichtung der nativen Bibliothek: Backend wählen, Log in eine Datei umleiten.</summary>
    private static partial class NativeSetup
    {
        private static readonly Lock Gate = new();
        private static StreamWriter? _log;
        private static bool _configured;

        /// <summary>Aus dem Log von llama.cpp: wie viele Schichten tatsächlich auf der GPU liegen.</summary>
        public static int? OffloadedLayers { get; private set; }

        public static void Configure(bool useGpu, string logFile)
        {
            lock (Gate)
            {
                if (_configured)
                    return;
                _configured = true;

                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                _log = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            }

            // Ohne das schreibt llama.cpp munter ins Terminal und zerschießt die Oberfläche.
            NativeLibraryConfig.All
                .WithCuda(false)
                .WithVulkan(useGpu)
                .WithAutoFallback(true)
                .WithLogCallback((level, message) => OnNativeLog(message));
        }

        public static void Log(string message) => OnNativeLog($"[max] {message}\n");

        private static void OnNativeLog(string message)
        {
            if (OffloadedRegex().Match(message) is { Success: true } match)
                OffloadedLayers = int.Parse(match.Groups[1].Value);
            lock (Gate)
                _log?.Write(message);
        }

        [GeneratedRegex(@"offloaded (\d+)/\d+ layers to GPU")]
        private static partial Regex OffloadedRegex();
    }
}
