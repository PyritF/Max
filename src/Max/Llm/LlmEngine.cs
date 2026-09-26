using System.Diagnostics;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Max.Setup;
using Max.Ui;

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
    // Wo die Logits für das nächste Token stehen: letzte Position des letzten Batches.
    private int _logitIndex;

    private LlmEngine(LLamaWeights weights, LLamaContext context, EngineInfo info)
    {
        _weights = weights;
        _context = context;
        Info = info;
    }

    public EngineInfo Info { get; }
    public int ContextSize => Info.ContextSize;
    public int CachedCount => _cached.Count;

    /// <summary>
    /// Lädt das Modell. Mit brauchbarer Grafikkarte über Vulkan, sonst auf der CPU.
    /// Scheitert es auf der Grafikkarte (z. B. zu wenig Speicher), gibt es einen zweiten Versuch auf der CPU.
    /// </summary>
    /// <param name="progress">Bekommt den Ladefortschritt in Bytes – für den Balken beim Start.</param>
    public static async Task<LlmEngine> LoadAsync(
        string modelPath, int contextSize, HardwareInfo hardware, string logFile, StepProgress? progress, CancellationToken ct)
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

        var loadProgress = new LoadProgress(progress, modelBytes);
        LLamaWeights weights;
        try
        {
            weights = await LLamaWeights.LoadFromFileAsync(parameters, ct, loadProgress);
        }
        catch (Exception e) when (layers > 0 && e is not OperationCanceledException)
        {
            NativeSetup.Log($"GPU-Laden gescheitert, versuche CPU: {e.Message}");
            parameters.GpuLayerCount = layers = 0;
            weights = await LLamaWeights.LoadFromFileAsync(parameters, ct, loadProgress);
        }
        progress?.Report(modelBytes, modelBytes, 0);

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

    public bool IsEndOfGeneration(int token) => ((LLamaToken)token).IsEndOfGeneration(_context.NativeHandle.Vocab);

    public ITokenDecoder CreateDecoder() => new Decoder(new StreamingTokenDecoder(_context));

    public ITokenSampler CreateSampler(SamplingSettings settings, string? grammar = null, uint? seed = null, IReadOnlyCollection<int>? banned = null) =>
        new GrammarSampler(_context.NativeHandle, () => _logitIndex, settings, grammar, seed, banned);

    /// <summary>
    /// Bringt den Kontext auf den Stand des Prompts. Liefert, wie viele Tokens wiederverwendet wurden.
    /// Weicht der Prompt vom Cache ab, wird der Kontext komplett neu gerechnet: Neuere Modelle
    /// (z. B. mit rekurrenten Schichten) lassen sich nicht auf eine beliebige Zwischenposition zurücksetzen –
    /// dafür gibt es <see cref="Checkpoint"/>.
    /// </summary>
    public async Task<int> PrefillAsync(IReadOnlyList<int> prompt, CancellationToken ct)
    {
        if (prompt.Count >= ContextSize)
            throw new InvalidOperationException($"Prompt ({prompt.Count} Tokens) passt nicht in den Kontext ({ContextSize}).");

        var common = 0;
        while (common < _cached.Count && common < prompt.Count && _cached[common] == prompt[common])
            common++;

        // Nur anhängen, wenn der ganze Cache passt und mindestens ein neues Token bleibt (für die Logits).
        if (common < _cached.Count || common == prompt.Count)
        {
            Clear();
            common = 0;
        }

        await AppendAsync(prompt.Skip(common).ToArray(), ct);
        return common;
    }

    public async Task AppendAsync(IReadOnlyList<int> tokens, CancellationToken ct)
    {
        if (tokens.Count == 0)
            return;
        if (_cached.Count + tokens.Count > ContextSize)
            throw new InvalidOperationException("Kontext voll.");

        var pending = tokens.Select(t => (LLamaToken)t).ToArray();
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
                    _logitIndex = batch.TokenCount - 1;
            }
        }
        catch
        {
            // Halb verarbeitet: lieber sauber von vorn beim nächsten Mal.
            Clear();
            throw;
        }
    }

    public ModelCheckpoint? Checkpoint()
    {
        try
        {
            return new EngineCheckpoint(_context.GetState(Sequence), _cached.ToArray(), _logitIndex);
        }
        catch (Exception e)
        {
            Log($"Zwischenstand konnte nicht gespeichert werden: {e.Message}");
            return null;
        }
    }

    public unsafe bool Restore(ModelCheckpoint checkpoint)
    {
        var saved = (EngineCheckpoint)checkpoint;
        try
        {
            var read = _context.NativeHandle.SetState((byte*)saved.State.DangerousGetHandle(), saved.State.Size, Sequence);
            if (read == 0)
                throw new InvalidOperationException("llama.cpp hat den Zustand nicht angenommen.");
            _cached.Clear();
            _cached.AddRange(saved.Tokens);
            _logitIndex = saved.LogitIndex;
            return true;
        }
        catch (Exception e)
        {
            Log($"Zwischenstand konnte nicht geladen werden, rechne neu: {e.Message}");
            Clear();
            return false;
        }
    }

    private void Clear()
    {
        _context.NativeHandle.MemoryClear(true);
        _cached.Clear();
    }

    private async Task DecodeAsync(LLamaBatch batch, CancellationToken ct)
    {
        // Rechnen auf einem Hintergrund-Thread, damit Spinner und Strg+C flüssig bleiben.
        var result = await Task.Run(() => _context.NativeHandle.Decode(batch), ct);
        if (result != DecodeResult.Ok)
            throw new InvalidOperationException($"llama.cpp: decode fehlgeschlagen ({result}).");
    }

    private sealed class EngineCheckpoint(LLamaContext.SequenceState state, int[] tokens, int logitIndex) : ModelCheckpoint
    {
        public LLamaContext.SequenceState State { get; } = state;
        public int[] Tokens { get; } = tokens;
        public int LogitIndex { get; } = logitIndex;
        public override int TokenCount => Tokens.Length;
        public override void Dispose() => State.Dispose();
    }

    private sealed class Decoder(StreamingTokenDecoder inner) : ITokenDecoder
    {
        public string Add(int token)
        {
            inner.Add((LLamaToken)token);
            return inner.Read();
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _weights.Dispose();
    }

    /// <summary>Übersetzt den Ladefortschritt von llama.cpp (0–1) in Bytes für den Balken.</summary>
    private sealed class LoadProgress(StepProgress? target, long totalBytes) : IProgress<float>
    {
        private readonly Setup.SpeedMeter _speed = new();

        public void Report(float value)
        {
            if (target is null)
                return;
            var done = (long)(Math.Clamp(value, 0, 1) * totalBytes);
            target.Report(done, totalBytes, _speed.Add(done));
        }
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
