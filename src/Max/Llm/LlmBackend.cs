using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Max.Chat;
using Max.Ui.Widgets;

namespace Max.Llm;

/// <summary>Messwerte der letzten Antwort – für /debug und den Selbsttest.</summary>
internal sealed record GenerationStats(
    int PromptTokens,
    int ReusedTokens,
    int GeneratedTokens,
    int ThinkingTokens,
    TimeSpan ThinkingTime,
    TimeSpan TimeToFirstToken,
    double TokensPerSecond,
    int Repairs);

/// <summary>Einstellungen für <see cref="LlmBackend"/>.</summary>
/// <param name="ThinkingBudget">Höchstens so viele Tokens Nachdenken, dann wird es beendet.</param>
/// <param name="ThinkingEnabled">Wird vor jeder Antwort gefragt (für /denken).</param>
/// <param name="UseGrammar">Antworten auf gültige Tags und Elemente festlegen (siehe <see cref="AnswerGrammar"/>).</param>
internal sealed record BackendOptions(
    int ThinkingBudget = 512,
    Func<bool>? ThinkingEnabled = null,
    bool UseGrammar = true,
    SamplingSettings? Answer = null,
    SamplingSettings? Thinking = null);

/// <summary>
/// Max' Antworten aus dem lokalen Sprachmodell: System-Prompt + Verlauf → Prompt → Modell → Text.
/// <list type="bullet">
/// <item>Nachdenken: Das Modell schreibt zuerst Gedanken (werden grau gezeigt), dann die Antwort.
/// Die Gedanken kommen nicht in den Verlauf: Nach der Antwort springt das Modell auf einen Zwischenstand
/// vor der Antwort zurück und rechnet nur die Antwort in Verlaufsform nach (ohne Denk-Block) –
/// so passt der Cache weiter Token für Token. Das gilt auch ohne Nachdenken.</item>
/// <item>Denk-Tags: In der Antwort sind sie gesperrt, und das Nachdenken endet nicht vor ein paar Tokens.</item>
/// <item>Grammatik: Die Antwort kann nur gültige Farb-Tags und Elemente enthalten.</item>
/// <item>Reparatur: Lässt sich ein Element trotzdem nicht zeichnen, wird es unsichtbar neu erzeugt.</item>
/// </list>
/// </summary>
internal sealed class LlmBackend : IChatBackend
{
    /// <summary>So viele Tokens bleiben im Kontext mindestens für die Antwort frei (plus Denk-Budget).</summary>
    internal const int AnswerReserve = 1024;

    /// <summary>So oft wird ein kaputtes Element höchstens neu erzeugt, dann wird es weggelassen.</summary>
    internal const int MaxRepairs = 2;

    /// <summary>Wird ein Element länger als das, hängt das Modell fest – dann wird abgebrochen.</summary>
    internal const int MaxElementChars = 3000;

    /// <summary>So viele Tokens denkt Max mindestens nach, bevor er das Nachdenken beenden darf.</summary>
    internal const int MinThinkingTokens = 8;

    /// <summary>Wiederholen sich die letzten so vielen Zeichen der Antwort wörtlich, steckt das Modell in einer Schleife.</summary>
    internal const int LoopChars = 200;

    private readonly ILanguageModel _model;
    private readonly string _systemPrompt;
    private readonly IChatTemplate _template;
    private readonly BackendOptions _options;
    private readonly SamplingSettings _answer;
    private readonly SamplingSettings _thinking;
    private readonly ContextWindow _window;

    // Tokens je Nachricht. Für eigene Antworten genau die Tokens, die auch im Cache des Modells stehen –
    // nicht neu zerlegt, damit der nächste Prompt exakt zum Cache passt.
    private readonly Dictionary<(ChatRole Role, string Content), IReadOnlyList<int>> _tokens = [];
    private IReadOnlyList<int>? _systemTokens;
    private IReadOnlyList<int>? _assistantStart;
    private IReadOnlyList<int>? _thinkingStart;
    private IReadOnlyList<int>? _assistantEnd;
    private IReadOnlyList<int>? _historyStart;
    private int[]? _thinkTags;          // <think> und </think>, falls je ein einzelnes Token
    private int[]? _thinkEnd;

    // Die Antwort in Verlaufsform wird nach der Ausgabe im Hintergrund nachgerechnet.
    private Task? _pendingCommit;

    public LlmBackend(ILanguageModel model, string systemPrompt, BackendOptions? options = null, IChatTemplate? template = null)
    {
        _model = model;
        _systemPrompt = systemPrompt;
        _template = template ?? new ChatMlTemplate();
        _options = options ?? new BackendOptions();
        _answer = _options.Answer ?? new SamplingSettings();
        _thinking = _options.Thinking ?? SamplingSettings.Thinking;
        _window = new ContextWindow(Math.Max(256, model.ContextSize - AnswerReserve - _options.ThinkingBudget));
    }

    /// <summary>Wie lange das Aufwärmen gedauert hat – für /debug.</summary>
    public TimeSpan? WarmUpTime { get; private set; }

    public GenerationStats? LastRun { get; private set; }

    public bool ThinkingEnabled => _options.ThinkingEnabled?.Invoke() ?? true;

    /// <summary>
    /// Rechnet den System-Prompt schon beim Start in den Cache. Dabei richtet sich auch die
    /// Grafikkarte ein (Vulkan übersetzt beim ersten Rechnen seine Shader) – die Wartezeit
    /// fällt so in den Ladebildschirm statt in die erste Antwort.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        _systemTokens ??= _model.Tokenize(_template.Message(ChatRole.System, _systemPrompt));
        await _model.PrefillAsync(_systemTokens, ct);
        WarmUpTime = clock.Elapsed;
    }

    public async IAsyncEnumerable<ReplyChunk> StreamReplyAsync(Conversation conversation, [EnumeratorCancellation] CancellationToken ct)
    {
        await FinishCommitAsync();

        var think = ThinkingEnabled;
        var prompt = BuildPrompt(conversation.Messages);
        var head = think ? _thinkingStart! : _assistantStart!;
        var clock = Stopwatch.StartNew();

        var reused = await _model.PrefillAsync(prompt, ct);
        var beforeReply = _model.Checkpoint();
        await _model.AppendAsync(head, ct);

        var splitter = new ThinkSplitter(startInThinking: think);
        if (think)
            yield return new ReplyChunk(_template.ThinkingSeed, IsThinking: true);
        var gate = new ElementGate();
        var closing = new ClosingFilter();
        var decoder = _model.CreateDecoder();
        var answerPhase = !think;
        // Anfangs darf das Nachdenken nicht gleich wieder enden – sonst denkt das Modell in der Antwort weiter.
        var sampler = answerPhase ? CreateAnswerSampler() : _model.CreateSampler(_thinking, banned: _thinkEnd);
        var thinkingFree = _thinkEnd!.Length == 0;

        var generated = new List<int>();        // alles, was nach dem Kopf im Cache steht
        var answerTokens = new List<int>();     // die Antwort (ab dem ersten sichtbaren Zeichen)
        var answerAll = new List<int>();        // alles seit Beginn der Antwort – zum Nachführen der Grammatik
        var shown = new StringBuilder();
        int thinkingTokens = 0, repairs = 0;
        TimeSpan thinkingTime = TimeSpan.Zero, firstToken = TimeSpan.Zero;
        Repair? repair = null;
        var genClock = new Stopwatch();

        try
        {
            while (answerTokens.Count < _answer.MaxTokens && _model.CachedCount + 1 < _model.ContextSize)
            {
                ct.ThrowIfCancellationRequested();
                var token = sampler.Sample();
                if (generated.Count == 0)
                {
                    firstToken = clock.Elapsed;
                    genClock.Start();
                }
                if (_model.IsEndOfGeneration(token))
                    break;
                var text = decoder.Add(token);

                // Vor dem Token, das die Kopfzeile eines Elements abschließt, einen Zwischenstand merken.
                ModelCheckpoint? headerPoint = answerPhase && gate.WouldOpenElement(text) ? _model.Checkpoint() : null;
                var before = (generated.Count, answerTokens.Count, answerAll.Count);

                // Erst verarbeiten, dann ausgeben: So passt der Cache immer genau zu dem, was der Nutzer gesehen hat.
                await _model.AppendAsync([token], ct);
                generated.Add(token);
                if (answerPhase)
                {
                    answerAll.Add(token);
                    if (!think || answerTokens.Count > 0 || text.Trim().Length > 0)
                        answerTokens.Add(token);
                }
                else
                {
                    thinkingTokens++;
                }

                foreach (var chunk in Route(splitter.Push(text), gate, closing, shown))
                    yield return chunk;

                if (answerPhase && text.Contains('\n') && IsLooping(shown))
                {
                    LlmEngine.Log("Antwort wiederholt sich, abgebrochen.");
                    break;
                }

                if (headerPoint is not null)
                {
                    repair?.Dispose();
                    repair = new Repair(headerPoint, token, gate.Save(), generated.Count, answerTokens.Count, answerAll.Count, before);
                }

                // Endlosschleife im Element (das Modell findet keinen gültigen Abschluss): zurück vor den Block, Antwort beenden.
                if (gate.InElement && gate.HeldLength > MaxElementChars)
                {
                    LlmEngine.Log("Element viel zu lang, abgebrochen.");
                    repairs++;
                    gate.Abandon();
                    if (repair is not null && _model.Restore(repair.Checkpoint))
                    {
                        Truncate(generated, repair.Before.Generated);
                        Truncate(answerTokens, repair.Before.AnswerTokens);
                        Truncate(answerAll, repair.Before.AnswerAll);
                    }
                    else
                    {
                        await _model.PrefillAsync([.. prompt, .. head, .. generated], ct);
                    }
                    if (shown.ToString().Trim().Length == 0)
                    {
                        // Lieber ein ehrlicher Satz als eine leere Antwort. Er steht nur in der Anzeige, nicht im Cache.
                        const string sorry = "Das wollte mir gerade nicht gelingen. Frag mich gern noch einmal, vielleicht etwas anders.";
                        shown.Append(sorry);
                        yield return new ReplyChunk(sorry);
                    }
                    break;
                }

                if (!answerPhase && !thinkingFree && thinkingTokens >= MinThinkingTokens)
                {
                    thinkingFree = true;
                    sampler.Dispose();
                    sampler = _model.CreateSampler(_thinking);
                }

                // Nachdenken vorbei (selbst beendet oder Budget aufgebraucht) → ab jetzt die Antwort mit Grammatik.
                if (!answerPhase && !splitter.ThinkingEnded && thinkingTokens >= _options.ThinkingBudget)
                {
                    var forced = _model.Tokenize(_template.ForcedThinkingEnd);
                    await _model.AppendAsync(forced, ct);
                    generated.AddRange(forced);
                    var forcedText = new StringBuilder();
                    foreach (var t in forced)
                        forcedText.Append(decoder.Add(t));
                    foreach (var chunk in Route(splitter.Push(forcedText.ToString()), gate, closing, shown))
                        yield return chunk;
                }
                if (!answerPhase && splitter.ThinkingEnded)
                {
                    answerPhase = true;
                    thinkingTime = clock.Elapsed;
                    sampler.Dispose();
                    sampler = CreateAnswerSampler();
                }

                // Ein Element ist fertig: gut → zeigen, kaputt → neu erzeugen oder weglassen.
                if (gate.Closed is { } closed)
                {
                    string released;
                    if (WidgetValidator.IsValid(closed.Name, closed.Body))
                    {
                        released = gate.Accept();
                    }
                    else if (repair is { Attempts: < MaxRepairs })
                    {
                        repairs++;
                        LlmEngine.Log($"Element '{closed.Name}' ungültig, erzeuge neu (Versuch {repair.Attempts + 1}): {closed.Body.ReplaceLineEndings(" / ")}");
                        if (_model.Restore(repair.Checkpoint))
                        {
                            await _model.AppendAsync([repair.HeaderToken], ct);
                            repair.Attempts++;
                            Truncate(generated, repair.Generated);
                            Truncate(answerTokens, repair.AnswerTokens);
                            Truncate(answerAll, repair.AnswerAll);
                            sampler.Dispose();
                            sampler = CreateAnswerSampler(temperature: 0.3f, seed: (uint)Random.Shared.Next());
                            foreach (var t in answerAll)
                                sampler.Accept(t);
                            decoder = _model.CreateDecoder();
                            gate.Load(repair.Gate);
                            continue;
                        }
                        // Zwischenstand kaputt: alles neu rechnen, Block weglassen.
                        await _model.PrefillAsync([.. prompt, .. head, .. generated], ct);
                        released = gate.Drop();
                    }
                    else
                    {
                        LlmEngine.Log($"Element '{closed.Name}' ungültig, weggelassen.");
                        released = gate.Drop();
                    }
                    repair?.Dispose();
                    repair = null;
                    released = closing.Push(released);
                    if (released.Length > 0)
                    {
                        shown.Append(released);
                        yield return new ReplyChunk(released);
                    }
                }
            }

            // Rest: angefangene Tags, offene Zeilen, ein nicht geschlossener Block.
            foreach (var chunk in Route(splitter.Flush(), gate, closing, shown))
                yield return chunk;
            var rest = gate.Flush();
            if (gate.Closed is { } open)
                rest += WidgetValidator.IsValid(open.Name, open.Body) ? gate.Accept() : gate.Drop();
            rest = closing.Push(rest) + closing.Flush();
            if (rest.Length > 0)
            {
                shown.Append(rest);
                yield return new ReplyChunk(rest);
            }
        }
        finally
        {
            sampler.Dispose();
            repair?.Dispose();
            var seconds = genClock.Elapsed.TotalSeconds;
            LastRun = new GenerationStats(prompt.Count + head.Count, reused, generated.Count, thinkingTokens,
                thinkingTime, firstToken, seconds > 0 ? (generated.Count - 1) / seconds : 0, repairs);
            Finish(beforeReply, head, generated, shown.ToString());
            beforeReply?.Dispose();
        }
    }

    /// <summary>
    /// Nach der Antwort: Den Cache so hinterlassen, wie der Verlauf beim nächsten Mal aussieht.
    /// Zurück vor die Antwort und sie in Verlaufsform (ohne Denk-Block) nachrechnen – im Hintergrund.
    /// </summary>
    private void Finish(ModelCheckpoint? beforeReply, IReadOnlyList<int> head, List<int> generated, string shown)
    {
        if (beforeReply is null)
        {
            // Ohne Zwischenstand bleibt der Kopf (samt Nachdenken) im Cache – dann eben auch im Verlauf (bleibt schnell).
            if (shown.Length > 0)
                Remember(shown, [.. head, .. generated, .. _assistantEnd!]);
            return;
        }

        // Im Verlauf steht genau, was der Nutzer gesehen hat – ohne verworfene Elemente oder abgebrochene Blöcke.
        var history = new List<int>([.. _historyStart!, .. _model.Tokenize(shown), .. _assistantEnd!]);
        if (!_model.Restore(beforeReply))
        {
            if (shown.Length > 0)
                Remember(shown, history);          // Cache ist leer, der nächste Prompt rechnet alles neu
            return;
        }
        if (shown.Length == 0)
            return;

        Remember(shown, history);
        _pendingCommit = _model.AppendAsync(history, CancellationToken.None);
    }

    /// <summary>Wartet, bis die letzte Antwort im Hintergrund fertig nachgerechnet ist.</summary>
    public Task CompleteAsync() => FinishCommitAsync();

    private async Task FinishCommitAsync()
    {
        if (_pendingCommit is not { } commit)
            return;
        _pendingCommit = null;
        try
        {
            await commit;
        }
        catch (Exception e)
        {
            LlmEngine.Log($"Nachrechnen der letzten Antwort gescheitert: {e.Message}");
        }
    }

    /// <summary>Verteilt Denk- und Antwort-Stücke: Denken direkt raus, Antwort durch die Element-Schleuse und den Floskel-Filter.</summary>
    private static IEnumerable<ReplyChunk> Route(List<(bool Thinking, string Text)> parts, ElementGate gate, ClosingFilter closing, StringBuilder shown)
    {
        foreach (var (thinking, text) in parts)
        {
            if (thinking)
            {
                yield return new ReplyChunk(text, IsThinking: true);
                continue;
            }
            var released = closing.Push(gate.Push(text));
            if (released.Length > 0)
            {
                shown.Append(released);
                yield return new ReplyChunk(released);
            }
        }
    }

    private ITokenSampler CreateAnswerSampler(float? temperature = null, uint? seed = null) =>
        _model.CreateSampler(
            temperature is { } t ? _answer with { Temperature = t } : _answer,
            _options.UseGrammar ? AnswerGrammar.Gbnf : null,
            seed,
            _thinkTags);

    /// <summary>System-Prompt und Verlauf – ohne den Beginn der Antwort (der hängt vom Nachdenken ab).</summary>
    internal IReadOnlyList<int> BuildPrompt(IReadOnlyList<ChatMessage> messages)
    {
        _systemTokens ??= _model.Tokenize(_template.Message(ChatRole.System, _systemPrompt));
        _assistantStart ??= _model.Tokenize(_template.AssistantStart);
        _thinkingStart ??= _model.Tokenize(_template.AssistantStartThinking + _template.ThinkingSeed);
        _assistantEnd ??= _model.Tokenize(_template.AssistantEnd);
        _historyStart ??= _model.Tokenize(_template.HistoryStart);
        if (_thinkTags is null)
        {
            _thinkEnd = SingleToken("</think>");
            _thinkTags = [.. SingleToken("<think>"), .. _thinkEnd];
        }

        var first = _window.FirstIncluded(messages, _systemTokens.Count + _thinkingStart.Count, m => TokensOf(m).Count);

        var prompt = new List<int>(_systemTokens);
        for (var i = first; i < messages.Count; i++)
            prompt.AddRange(TokensOf(messages[i]));
        return prompt;
    }

    private IReadOnlyList<int> TokensOf(ChatMessage message)
    {
        var key = (message.Role, message.Content);
        if (!_tokens.TryGetValue(key, out var tokens))
        {
            tokens = _model.Tokenize(_template.Message(message.Role, message.Content));
            _tokens[key] = tokens;
        }
        return tokens;
    }

    /// <summary>Stehen die letzten <see cref="LoopChars"/> Zeichen schon einmal weiter vorn? Dann dreht sich die Antwort im Kreis.</summary>
    internal static bool IsLooping(StringBuilder shown)
    {
        if (shown.Length < 2 * LoopChars)
            return false;
        var text = shown.ToString();
        var tail = text[^LoopChars..];
        return tail.Trim().Length > LoopChars / 2 && text.IndexOf(tail, StringComparison.Ordinal) < text.Length - LoopChars;
    }

    private int[] SingleToken(string text) => _model.Tokenize(text) is [var token] ? [token] : [];

    private void Remember(string content, IReadOnlyList<int> tokens) => _tokens[(ChatRole.Assistant, content)] = tokens;

    private static void Truncate(List<int> list, int count) => list.RemoveRange(count, list.Count - count);

    /// <summary>Wohin es zurückgeht, wenn ein Element neu erzeugt werden muss.</summary>
    private sealed class Repair(
        ModelCheckpoint checkpoint, int headerToken, ElementGate.Snapshot gate, int generated, int answerTokens, int answerAll,
        (int Generated, int AnswerTokens, int AnswerAll) before) : IDisposable
    {
        /// <summary>Stand vor dem Token mit der Kopfzeile – so weit geht es zurück, wenn das Element ganz wegfällt.</summary>
        public (int Generated, int AnswerTokens, int AnswerAll) Before { get; } = before;
        public ModelCheckpoint Checkpoint { get; } = checkpoint;
        public int HeaderToken { get; } = headerToken;
        public ElementGate.Snapshot Gate { get; } = gate;
        public int Generated { get; } = generated;
        public int AnswerTokens { get; } = answerTokens;
        public int AnswerAll { get; } = answerAll;
        public int Attempts { get; set; }
        public void Dispose() => Checkpoint.Dispose();
    }
}
