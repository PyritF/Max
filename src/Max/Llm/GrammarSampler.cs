using LLama.Native;

namespace Max.Llm;

/// <summary>
/// Zieht Tokens wie üblich (Wiederholungsstrafe, Top-k, Top-p, Temperatur) – optional mit einer
/// Grammatik, die nur gültige Fortsetzungen erlaubt. Schnell: Erst wird normal gezogen und nur
/// dieses eine Token gegen die Grammatik geprüft; nur wenn es ungültig ist, filtert die Grammatik
/// das ganze Vokabular. Die Grammatik wird sofort angelegt, damit <see cref="Accept"/> sie auch
/// für eingefügte Tokens nachführt (z. B. nach dem Wiederherstellen eines Zwischenstands).
/// Gesperrte Tokens (z. B. Denk-Tags in der Antwort) werden nie gezogen.
/// </summary>
internal sealed class GrammarSampler : ITokenSampler
{
    private readonly SafeLLamaContextHandle _context;
    private readonly Func<int> _logitIndex;
    private readonly SafeLLamaSamplerChainHandle _chain;
    private readonly SafeLLamaSamplerChainHandle? _grammar;
    private readonly LLamaTokenData[] _buffer;
    private readonly LLamaTokenData[] _single = new LLamaTokenData[1];

    public GrammarSampler(SafeLLamaContextHandle context, Func<int> logitIndex, SamplingSettings settings, string? grammar, uint? seed, IReadOnlyCollection<int>? banned = null)
    {
        _context = context;
        _logitIndex = logitIndex;
        _buffer = new LLamaTokenData[context.ModelHandle.Vocab.Count];

        _chain = SafeLLamaSamplerChainHandle.Create(LLamaSamplerChainParams.Default());
        if (banned is { Count: > 0 })
        {
            var vocab = context.ModelHandle.Vocab.Count;
            _chain.AddLogitBias(vocab, banned.Select(t => new LLamaLogitBias { Token = (LLamaToken)t, Bias = float.NegativeInfinity }).ToArray());
        }
        _chain.AddPenalties(64, settings.RepeatPenalty, 0, 0);
        _chain.AddTopK(settings.TopK);
        _chain.AddTopP(settings.TopP, 1);
        _chain.AddMinP(settings.MinP, 1);
        _chain.AddTemperature(settings.Temperature);
        _chain.AddDistributionSampler(seed ?? (uint)Random.Shared.Next());

        // Nur ASCII: Nicht-ASCII käme unter Windows verstümmelt an, llama.cpp lieferte keinen Sampler,
        // und der erste Zugriff darauf wäre ein Absturz, den .NET nicht abfangen kann (siehe AnswerGrammar.AsciiOnly).
        if (grammar is not null && grammar.Any(c => c >= 128))
        {
            LlmEngine.Log("Grammatik enthält Nicht-ASCII-Zeichen – ohne Grammatik weiter.");
            grammar = null;
        }

        if (grammar is not null)
        {
            try
            {
                var chain = SafeLLamaSamplerChainHandle.Create(LLamaSamplerChainParams.Default());
                chain.AddGrammar(context.ModelHandle, grammar, "root");
                _grammar = chain;
            }
            catch (Exception e)
            {
                LlmEngine.Log($"Grammatik nicht geladen, ohne weiter: {e.Message}");
            }
        }
    }

    public bool HasGrammar => _grammar is not null;

    public int Sample()
    {
        var index = _logitIndex();
        if (_grammar is null)
            return (int)_chain.Sample(_context, index); // nimmt das Token selbst an

        // 1. Normal ziehen, dann nur dieses Token prüfen.
        var array = LLamaTokenDataArray.Create(_context.GetLogitsIth(index), _buffer);
        using (LLamaTokenDataArrayNative.Create(array, out var native))
        {
            _chain.Apply(ref native);
            var token = native.Data[(int)native.Selected].ID;
            if (IsAllowed(token))
            {
                Accept((int)token);
                return (int)token;
            }
        }

        // 2. Ungültig: Grammatik auf alles anwenden, dann erneut ziehen.
        array = LLamaTokenDataArray.Create(_context.GetLogitsIth(index), _buffer);
        using (LLamaTokenDataArrayNative.Create(array, out var native))
        {
            _grammar.Apply(ref native);
            _chain.Apply(ref native);
            var token = (int)native.Data[(int)native.Selected].ID;
            Accept(token);
            return token;
        }
    }

    public void Accept(int token)
    {
        _chain.Accept((LLamaToken)token);
        _grammar?.Accept((LLamaToken)token);
    }

    private bool IsAllowed(LLamaToken token)
    {
        _single[0] = new LLamaTokenData(token, 1f, 0f);
        using (LLamaTokenDataArrayNative.Create(new LLamaTokenDataArray(_single, isSorted: true), out var native))
        {
            _grammar!.Apply(ref native);
            return !float.IsNegativeInfinity(native.Data[0].Logit);
        }
    }

    public void Dispose()
    {
        _chain.Dispose();
        _grammar?.Dispose();
    }
}
