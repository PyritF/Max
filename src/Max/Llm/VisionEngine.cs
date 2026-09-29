using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;

namespace Max.Llm;

/// <summary>Sieht sich ein Bild an und antwortet in Worten – das Gesprächsmodell bekommt nur diesen Text.</summary>
internal interface IVision
{
    /// <summary>Beschreibt das Bild oder beantwortet <paramref name="question"/> dazu.</summary>
    Task<string> LookAsync(string imagePath, string question, CancellationToken ct);
}

/// <summary>
/// Bildverständnis mit dem Bild-Zusatz des Modells (<c>mmproj</c>, PLAN.md §9b). Das Bild kommt nicht in den
/// Gesprächs-Cache: Für jedes Bild gibt es einen eigenen, kleinen Kontext auf denselben Gewichten, darin
/// Bild + Frage, und die Antwort geht als Werkzeug-Ergebnis zurück ins Gespräch. So bleibt der Verlauf reiner
/// Text, und der Zwischenspeicher des Gesprächs passt weiter Token für Token.
/// Der Zusatz wird erst beim ersten Bild geladen und bleibt dann für die Sitzung.
/// </summary>
internal sealed class VisionEngine(LlmEngine engine, string projectorPath, bool useGpu) : IVision, IDisposable
{
    /// <summary>Bild (höchstens <see cref="MaxImageTokens"/>) + Frage + Antwort.</summary>
    internal const int ContextSize = 4096;

    /// <summary>Große Bilder werden kleiner gerechnet – mehr Tokens kosten nur Zeit.</summary>
    internal const int MaxImageTokens = 1024;

    /// <summary>Die Antwort ist nur Zwischenstand für das Gesprächsmodell – kurz genügt.</summary>
    internal const int MaxAnswerTokens = 600;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private MtmdWeights? _projector;

    public async Task<string> LookAsync(string imagePath, string question, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_projector is null)
            {
                var parameters = MtmdContextParams.Default();   // lädt nebenbei die Bibliothek
                QuietNativeLog();
                parameters.UseGpu = useGpu;
                parameters.PrintTimings = false;
                parameters.Warmup = false;
                parameters.ImageMaxTokens = MaxImageTokens;
                parameters.NThreads = Math.Max(1, Environment.ProcessorCount / 2);
                _projector = await MtmdWeights.LoadFromFileAsync(projectorPath, engine.Weights, parameters, ct);
                if (!_projector.SupportsVision)
                    throw new InvalidOperationException("Der Zusatz kann keine Bilder.");
                LlmEngine.Log($"Bild-Zusatz geladen ({(useGpu ? "Grafikkarte" : "CPU")}).");
            }
            return await Task.Run(() => Look(_projector, imagePath, question, ct), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Look(MtmdWeights projector, string imagePath, string question, CancellationToken ct)
    {
        using var context = engine.CreateSideContext(ContextSize, batchSize: MaxImageTokens * 2);
        using var image = projector.LoadMedia(imagePath);

        // Wie eine normale Nachricht (ChatML), das Bild an der Stelle der Marke; ohne Nachdenken.
        var marker = MtmdContextParams.Default().MediaMarker ?? "<__media__>";
        var prompt = $"<|im_start|>user\n{marker}\n{question}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";
        var status = projector.Tokenize(prompt, addSpecial: false, parseSpecial: true, [image], out var chunks);
        if (status != 0 || chunks is null)
            throw new InvalidOperationException($"Bild ließ sich nicht vorbereiten ({status}).");

        using (chunks)
        {
            var past = 0;
            status = projector.EvaluateChunks(chunks, context.NativeHandle, ref past, 0, (int)context.BatchSize, logitsLast: true);
            if (status != 0)
                throw new InvalidOperationException($"Bild ließ sich nicht verarbeiten ({status}).");

            // -1 = Logits der letzten Position – nach dem Bild wie nach jedem einzelnen Token.
            using var sampler = new GrammarSampler(context.NativeHandle, () => -1, new SamplingSettings(Temperature: 0.3f), null, null);
            var decoder = new StreamingTokenDecoder(context);
            var answer = new StringBuilder();
            var batch = new LLamaBatch();
            for (var i = 0; i < MaxAnswerTokens && past + 1 < ContextSize; i++)
            {
                ct.ThrowIfCancellationRequested();
                var token = (LLamaToken)sampler.Sample();
                if (token.IsEndOfGeneration(context.NativeHandle.Vocab))
                    break;
                decoder.Add(token);
                answer.Append(decoder.Read());
                batch.Clear();
                batch.Add(token, new LLamaPos { Value = past++ }, LLamaSeqId.Zero, logits: true);
                if (context.NativeHandle.Decode(batch) != DecodeResult.Ok)
                    break;
            }
            return answer.ToString().Replace("<think>", "").Replace("</think>", "").Trim();
        }
    }

    private static bool _quiet;

    /// <summary>
    /// Der Bild-Teil von llama.cpp (mtmd) schreibt sein Protokoll sonst direkt ins Terminal und zerschießt die
    /// Oberfläche. LLamaSharp bietet dafür keinen Haken – also selbst umleiten, ins Protokoll (logs/llama.log).
    /// </summary>
    private static unsafe void QuietNativeLog()
    {
        if (_quiet)
            return;
        _quiet = true;
        try
        {
            var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .FirstOrDefault(m => m.ModuleName.Contains("mtmd", StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                LlmEngine.Log("Bild-Bibliothek nicht gefunden – ihr Protokoll bleibt im Terminal.");
                return;
            }
            var library = NativeLibrary.Load(module.FileName);
            delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, void> callback = &OnNativeLog;
            foreach (var name in new[] { "mtmd_log_set", "mtmd_helper_log_set" })
            {
                if (NativeLibrary.TryGetExport(library, name, out var set))
                    ((delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, IntPtr, IntPtr, void>, IntPtr, void>)set)(callback, IntPtr.Zero);
            }
        }
        catch (Exception e)
        {
            LlmEngine.Log($"Protokoll der Bild-Bibliothek nicht umgeleitet: {e.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeLog(int level, IntPtr text, IntPtr user)
    {
        if (text != IntPtr.Zero)
            LlmEngine.LogNative(Marshal.PtrToStringUTF8(text) ?? "");
    }

    public void Dispose()
    {
        _projector?.Dispose();
        _gate.Dispose();
    }
}
