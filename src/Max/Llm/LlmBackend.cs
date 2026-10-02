using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Max.Chat;
using Max.Ui;
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
    SamplingSettings? Thinking = null,
    PromptCache? PromptCache = null,
    int StablePromptLength = 0,
    Tools.ToolBox? Tools = null,
    int MaxToolRounds = 3);

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
internal sealed partial class LlmBackend : IChatBackend
{
    /// <summary>So viele Tokens bleiben im Kontext mindestens für die Antwort frei (plus Denk-Budget).</summary>
    internal const int AnswerReserve = 1024;

    /// <summary>So oft wird ein kaputtes Element höchstens neu erzeugt, dann wird es weggelassen.</summary>
    internal const int MaxRepairs = 2;

    /// <summary>
    /// Wird ein Element länger als das, hängt das Modell fest – dann fällt es weg. (Dreht es sich schon vorher
    /// erkennbar im Kreis, früher.)
    /// </summary>
    internal const int MaxElementChars = 6000;

    /// <summary>So viele Tokens denkt Max mindestens nach, bevor er das Nachdenken beenden darf.</summary>
    internal const int MinThinkingTokens = 8;

    /// <summary>Länger ist ein Werkzeug-Aufruf nie (Kopfzeile, Name, Angabe).</summary>
    internal const int MaxToolCallChars = 400;

    /// <summary>Wiederholen sich die letzten so vielen Zeichen der Antwort wörtlich, steckt das Modell in einer Schleife.</summary>
    internal const int LoopChars = 200;

    private readonly ILanguageModel _model;
    private string _systemPrompt;
    private readonly IChatTemplate _template;
    private readonly BackendOptions _options;
    private readonly SamplingSettings _answer;
    private readonly SamplingSettings _thinking;
    private readonly ContextWindow _window;
    private readonly Dictionary<(bool Tools, bool Colorful, bool Widgets, bool ToolOnly), string> _grammars = [];

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
        WarmUpFromCache = false;
        // Der feste Teil des System-Prompts liegt gerechnet auf der Platte – dann bleibt nur der Rest (Datum, Name …).
        if (_options.PromptCache is { } cache && StableTokens() is { } stable)
        {
            WarmUpFromCache = cache.TryLoad(_model, stable);
            if (!WarmUpFromCache)
            {
                await _model.PrefillAsync(stable, ct);
                cache.Save(_model, stable);
            }
        }
        await _model.PrefillAsync(_systemTokens, ct);
        WarmUpTime = clock.Elapsed;
    }

    /// <summary>Ob das letzte Aufwärmen den gespeicherten Stand nutzen konnte – für /debug und den Selbsttest.</summary>
    public bool WarmUpFromCache { get; private set; }

    /// <summary>Die Tokens des festen Anfangs – nur, wenn der ganze System-Prompt genau damit beginnt.</summary>
    private IReadOnlyList<int>? StableTokens()
    {
        if (_options.StablePromptLength <= 0)
            return null;
        var message = _template.Message(ChatRole.System, _systemPrompt);
        var start = message.IndexOf(_systemPrompt, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var stable = _model.Tokenize(message[..(start + _options.StablePromptLength)]);
        if (stable.Count >= _systemTokens!.Count || !_systemTokens.Take(stable.Count).SequenceEqual(stable))
        {
            LlmEngine.Log("Fester Teil des System-Prompts endet nicht an einer Token-Grenze – ohne Zwischenspeicher.");
            return null;
        }
        return stable;
    }

    /// <summary>
    /// Max' Antwort – bei Bedarf in mehreren Runden: Ruft das Modell ein Werkzeug auf, wird es ausgeführt,
    /// Aufruf und Ergebnis kommen in den Verlauf, und das Modell antwortet (oder ruft das nächste auf).
    /// Nach <see cref="BackendOptions.MaxToolRounds"/> Aufrufen muss es antworten.
    /// </summary>
    public async IAsyncEnumerable<ReplyChunk> StreamReplyAsync(Conversation conversation, [EnumeratorCancellation] CancellationToken ct)
    {
        // Ins Terminal gezogene Dateien sieht Max sich an, bevor er antwortet – wie ein Werkzeug-Aufruf im Verlauf.
        var request = conversation.LastUserMessage?.Content;
        if (_options.Tools is { } box && conversation.Messages.LastOrDefault() is { Role: ChatRole.User } asked)
        {
            foreach (var call in box.AttachmentCalls(asked.Content))
            {
                yield return new ReplyChunk(box.Find(call.Name)!.Describe(call.Argument), IsTool: true);
                var result = await box.RunAsync(call, ct, request);
                conversation.Add(ChatRole.Assistant, call.Text);
                conversation.Add(ChatRole.Tool, result);
            }
        }

        for (var round = 0; ; round++)
        {
            var toolCall = new ToolCallSlot();
            var allowTools = _options.Tools is not null && round < _options.MaxToolRounds;
            await foreach (var chunk in RoundAsync(conversation, allowTools, toolCall, ct))
                yield return chunk;
            if (toolCall.Call is not { } call)
                yield break;

            var tools = _options.Tools!;
            yield return new ReplyChunk(tools.Find(call.Name)?.Describe(call.Argument) ?? call.Name, IsTool: true);
            var result = await tools.RunAsync(call, ct, request);
            conversation.Add(ChatRole.Assistant, call.Text);
            conversation.Add(ChatRole.Tool, result);
        }
    }

    /// <summary>Hier landet ein Werkzeug-Aufruf aus einer Runde.</summary>
    private sealed class ToolCallSlot
    {
        public Tools.ToolCall? Call { get; set; }
    }

    /// <summary>Eine Runde: Prompt aus dem Verlauf, Nachdenken, Antwort – oder ein Werkzeug-Aufruf statt der Antwort.</summary>
    private async IAsyncEnumerable<ReplyChunk> RoundAsync(Conversation conversation, bool allowTools, ToolCallSlot toolCall, [EnumeratorCancellation] CancellationToken ct)
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
        var gate = new ElementGate { HoldFirstFence = allowTools };
        var closing = new ClosingFilter();
        var decoder = _model.CreateDecoder();
        var answerPhase = !think;
        // Anfangs darf das Nachdenken nicht gleich wieder enden – sonst denkt das Modell in der Antwort weiter.
        var colorful = WantsColors(conversation.Messages);
        var widgets = true;
        var codeReleased = false;           // ein Code-Block vom Anfang floss als Text weiter (Werkzeug-Zweig der Grammatik)
        var grammar = Grammar(allowTools, colorful, widgets);
        var sampler = answerPhase ? CreateAnswerSampler(grammar) : _model.CreateSampler(_thinking, banned: _thinkEnd);
        var thinkingFree = _thinkEnd!.Length == 0;

        var generated = new List<int>();        // alles, was nach dem Kopf im Cache steht
        var answerTokens = new List<int>();     // die Antwort (ab dem ersten sichtbaren Zeichen)
        var answerAll = new List<int>();        // alles seit Beginn der Antwort – zum Nachführen der Grammatik
        var shown = new StringBuilder();
        var thought = new StringBuilder();
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
                {
                    if (chunk.IsThinking)
                        thought.Append(chunk.Text);
                    yield return chunk;
                }

                if (answerPhase && text.IndexOfAny(LoopCheckChars) >= 0 && IsLooping(shown.ToString()))
                {
                    LlmEngine.Log("Antwort wiederholt sich, abgebrochen.");
                    break;
                }

                if (headerPoint is not null)
                {
                    repair?.Dispose();
                    repair = new Repair(headerPoint, token, gate.Save(), generated.Count, answerTokens.Count, answerAll.Count, before);
                }

                // Ein langer Code-Block am Anfang ist kein Werkzeug-Aufruf – dann fließt er als Code weiter.
                if (gate.Element == Tools.ToolCall.MaybeBlockName && gate.HeldLength > MaxToolCallChars)
                {
                    codeReleased = true;
                    var code = closing.Push(gate.Release());
                    if (code.Length > 0)
                    {
                        shown.Append(code);
                        yield return new ReplyChunk(code);
                    }
                }

                // Endlosschleife im Element (das Modell findet keinen gültigen Abschluss): Element weglassen und die
                // Antwort ohne Elemente weiterschreiben lassen – geht das nicht, zurück vor den Block und Antwort beenden.
                if (gate.InElement && gate.Element != Tools.ToolCall.MaybeBlockName
                    && (gate.HeldLength > MaxElementChars || text.Contains('\n') && IsLooping(gate.HeldText)))
                {
                    var element = gate.Element!;
                    repairs++;
                    gate.Abandon();
                    if (widgets && !codeReleased)
                    {
                        var keep = WithoutElement(answerAll, element);
                        if (keep is not null)
                        {
                            repair?.Dispose();
                            repair = null;
                            LlmEngine.Log($"Element '{element}' hängt fest, weggelassen – die Antwort geht ohne Elemente weiter.");
                            Truncate(generated, generated.Count - answerAll.Count);
                            generated.AddRange(keep);
                            answerAll.Clear();
                            answerAll.AddRange(keep);
                            Truncate(answerTokens, Math.Min(answerTokens.Count, keep.Count));
                            await _model.PrefillAsync([.. prompt, .. head, .. generated], ct);
                            widgets = false;
                            grammar = Grammar(allowTools, colorful, widgets);
                            sampler.Dispose();
                            sampler = CreateAnswerSampler(grammar);
                            foreach (var t in answerAll)
                                sampler.Accept(t);
                            decoder = _model.CreateDecoder();
                            continue;
                        }
                    }
                    LlmEngine.Log("Element viel zu lang, abgebrochen.");
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
                    {
                        if (chunk.IsThinking)
                            thought.Append(chunk.Text);
                        yield return chunk;
                    }
                }
                if (!answerPhase && splitter.ThinkingEnded)
                {
                    answerPhase = true;
                    thinkingTime = clock.Elapsed;
                    // Hat Max beim Nachdenken beschlossen, ein Werkzeug zu benutzen ("Ich verwende `rechnen`"), dann
                    // auch wirklich – sonst behauptet er es nur und rechnet im Kopf. Nur vor dem ersten Ergebnis.
                    if (allowTools && conversation.Messages.LastOrDefault()?.Role != ChatRole.Tool
                        && _options.Tools!.IntendedTool(thought.ToString()) is { } intended)
                    {
                        LlmEngine.Log($"Beim Nachdenken für '{intended}' entschieden – Antwort beginnt mit dem Aufruf.");
                        grammar = Grammar(allowTools, colorful, widgets, toolOnly: true);
                    }
                    sampler.Dispose();
                    sampler = CreateAnswerSampler(grammar);
                }

                // Ein Element ist fertig: gut → zeigen, kaputt → neu erzeugen oder weglassen.
                if (gate.Closed is { } closed)
                {
                    // Ein Werkzeug-Aufruf wird nie gezeigt: Runde beenden, StreamReplyAsync führt ihn aus.
                    if (closed.Name is Tools.ToolCall.BlockName or Tools.ToolCall.MaybeBlockName)
                    {
                        var call = allowTools ? Tools.ToolCall.Parse(closed.Body) : null;
                        if (call is not null && _options.Tools!.Find(call.Name) is not null)
                        {
                            toolCall.Call = call;
                            gate.Drop();
                            break;
                        }
                        if (closed.Name == Tools.ToolCall.BlockName)
                        {
                            gate.Drop();
                            continue;
                        }
                        // Doch kein Aufruf, sondern Code: ganz normal zeigen.
                        var code = closing.Push(gate.Accept());
                        if (code.Length > 0)
                        {
                            shown.Append(code);
                            yield return new ReplyChunk(code);
                        }
                        continue;
                    }

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
                            sampler = CreateAnswerSampler(grammar, temperature: 0.3f, seed: (uint)Random.Shared.Next());
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
            {
                // Das Modell hört oft direkt nach dem schließenden ``` auf (ohne Zeilenumbruch) – dann ist der Block
                // erst hier zu. Ein Werkzeug-Aufruf gilt trotzdem.
                if (open.Name is Tools.ToolCall.BlockName or Tools.ToolCall.MaybeBlockName
                    && allowTools && Tools.ToolCall.Parse(open.Body) is { } call && _options.Tools!.Find(call.Name) is not null)
                {
                    toolCall.Call = call;
                    gate.Drop();
                }
                else if (open.Name == Tools.ToolCall.MaybeBlockName)
                {
                    rest += gate.Accept();          // doch kein Aufruf: als Code zeigen
                }
                else
                {
                    rest += WidgetValidator.IsValid(open.Name, open.Body) ? gate.Accept() : gate.Drop();
                }
            }
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
            Finish(beforeReply, head, generated, shown.ToString(), WantsColors(conversation.Messages));
            beforeReply?.Dispose();
        }
    }

    /// <summary>
    /// Nach der Antwort: Den Cache so hinterlassen, wie der Verlauf beim nächsten Mal aussieht.
    /// Zurück vor die Antwort und sie in Verlaufsform (ohne Denk-Block) nachrechnen – im Hintergrund.
    /// </summary>
    private void Finish(ModelCheckpoint? beforeReply, IReadOnlyList<int> head, List<int> generated, string shown, bool keepColors)
    {
        if (beforeReply is null)
        {
            // Ohne Zwischenstand bleibt der Kopf (samt Nachdenken) im Cache – dann eben auch im Verlauf (bleibt schnell).
            if (shown.Length > 0)
                Remember(shown, [.. head, .. generated, .. _assistantEnd!]);
            return;
        }

        // Im Verlauf steht, was der Nutzer gesehen hat – ohne verworfene Elemente oder abgebrochene Blöcke.
        // Farb-Tags nur, wenn er Farben wollte: Sonst färbt eine zufällig bunte Antwort alle weiteren mit.
        // Antwortmöglichkeiten nach einer Schlussfrage ("Was willst du machen? / - Plaudern / - Arbeiten") nur zeigen,
        // nicht merken: Sonst hängt das Modell ab da an jede Antwort so ein Menü aus Text.
        var remembered = ClosingFilter.WithoutTrailingOptions(keepColors ? shown : ColorTags.Strip(shown));
        var history = new List<int>([.. _historyStart!, .. _model.Tokenize(remembered), .. _assistantEnd!]);
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

    /// <summary>
    /// Tauscht den System-Prompt, z. B. nachdem Max etwas vergessen soll. Beim nächsten Prompt rechnet das Modell
    /// ab der ersten Änderung neu – das Gedächtnis steht am Ende des System-Prompts, also nur wenig.
    /// </summary>
    public void UpdateSystemPrompt(string systemPrompt)
    {
        _systemPrompt = systemPrompt;
        _systemTokens = null;
    }

    /// <summary>
    /// Ein Auftrag im Hintergrund, z. B. das Gedächtnis am Ende eines Gesprächs: Der Verlauf plus
    /// <paramref name="instruction"/> als letzte Nachricht, ohne Nachdenken, optional in fester Form (Grammatik).
    /// Nichts davon kommt in den Verlauf.
    /// </summary>
    /// <param name="done">Hört auf, sobald der Text damit fertig ist.</param>
    public async Task<string> RunTaskAsync(IReadOnlyList<ChatMessage> messages, string instruction, string? grammar, int maxTokens, Func<string, bool>? done, CancellationToken ct)
    {
        await FinishCommitAsync();
        var prompt = BuildPrompt([.. messages, new ChatMessage(ChatRole.User, instruction, DateTime.Now)]);
        await _model.PrefillAsync([.. prompt, .. _assistantStart!], ct);

        using var sampler = _model.CreateSampler(_answer with { Temperature = 0.3f }, grammar, banned: _thinkTags);
        var decoder = _model.CreateDecoder();
        var text = new StringBuilder();
        for (var i = 0; i < maxTokens && _model.CachedCount + 1 < _model.ContextSize; i++)
        {
            ct.ThrowIfCancellationRequested();
            var token = sampler.Sample();
            if (_model.IsEndOfGeneration(token))
                break;
            text.Append(decoder.Add(token));
            await _model.AppendAsync([token], ct);
            if (done?.Invoke(text.ToString()) == true)
                break;
        }
        return text.ToString();
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

    private ITokenSampler CreateAnswerSampler(string? grammar, float? temperature = null, uint? seed = null) =>
        _model.CreateSampler(
            temperature is { } t ? _answer with { Temperature = t } : _answer,
            _options.UseGrammar ? grammar ?? AnswerGrammar.Gbnf : null,
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

        var fixedCost = _systemTokens.Count + _thinkingStart.Count;
        var first = _window.FirstIncluded(messages, fixedCost, m => TokensOf(m).Count);
        // Die letzte Frage des Nutzers bleibt immer – sonst antwortet Max auf ein Werkzeug-Ergebnis ohne Frage.
        var question = LastIndexOf(messages, ChatRole.User);
        if (question >= 0 && first > question)
            first = question;
        var included = messages.Skip(first).ToList();
        FitTurn(included, fixedCost);

        var prompt = new List<int>(_systemTokens);
        foreach (var message in included)
            prompt.AddRange(TokensOf(message));
        return prompt;
    }

    /// <summary>So viel bleibt von einem Werkzeug-Ergebnis mindestens, wenn der Platz knapp wird.</summary>
    internal const int MinToolChars = 400;

    /// <summary>
    /// Passt selbst die laufende Runde nicht in den Kontext (mehrere lange Dokumente, Webseiten …), werden ihre
    /// Werkzeug-Ergebnisse gekürzt – das längste zuerst. Nur im Prompt; im Verlauf bleibt alles, wie es war.
    /// Immer gleich gekürzt, damit der Cache in der nächsten Runde weiter passt.
    /// </summary>
    private void FitTurn(List<ChatMessage> included, int fixedCost)
    {
        var total = fixedCost + included.Sum(m => TokensOf(m).Count);
        while (total > _window.Budget)
        {
            var tools = included.Select((message, index) => (Message: message, Index: index))
                .Where(x => x.Message.Role == ChatRole.Tool && x.Message.Content.Length > MinToolChars + 100).ToList();
            if (tools.Count == 0)
                return;
            var longest = tools.MaxBy(x => TokensOf(x.Message).Count);
            var tokens = TokensOf(longest.Message).Count;
            var keep = Math.Max(MinToolChars, (int)(longest.Message.Content.Length * (tokens - (total - _window.Budget) - 60) / (double)tokens));
            if (keep >= longest.Message.Content.Length - 100)
                keep = Math.Max(MinToolChars, longest.Message.Content.Length / 2);
            included[longest.Index] = longest.Message with
            {
                Content = longest.Message.Content[..keep] + "\n… (gekürzt – für alles reicht der Platz im Gespräch nicht)",
            };
            total = fixedCost + included.Sum(m => TokensOf(m).Count);
        }
    }

    private static int LastIndexOf(IReadOnlyList<ChatMessage> messages, ChatRole role)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == role)
                return i;
        return -1;
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
    internal static bool IsLooping(string text)
    {
        if (text.Length < 2 * LoopChars)
            return false;
        var tail = text[^LoopChars..];
        return tail.Trim().Length > LoopChars / 2 && text.IndexOf(tail, StringComparison.Ordinal) < text.Length - LoopChars;
    }

    /// <summary>Nach Zeilen- und Satzenden auf Wiederholung prüfen – auch Schleifen ohne Zeilenumbruch fallen so auf.</summary>
    private static readonly char[] LoopCheckChars = ['\n', '.', '!', '?'];

    /// <summary>
    /// Will der Nutzer es gerade bunt ("schreib bunt", "mit Farbverläufen")? Es zählt sein letzter Satz dazu –
    /// "keine Farben mehr" oder "wieder normal" nimmt den Wunsch zurück.
    /// </summary>
    internal static bool WantsColors(IEnumerable<ChatMessage> messages) =>
        messages.LastOrDefault(m => m.Role == ChatRole.User && ColorWishRegex().IsMatch(m.Content)) is { } wish
        && !ColorStopRegex().IsMatch(wish.Content);

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(nicht|keine?n?|ohne|schluss|genug|weniger|normal|schlicht|aufhören|weg)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ColorStopRegex();

    /// <summary>Die Grammatik für eine Runde – je Kombination nur einmal gebaut.</summary>
    private string Grammar(bool tools, bool colorful, bool widgets, bool toolOnly = false)
    {
        tools &= _options.Tools is not null;
        toolOnly &= tools;
        if (!_grammars.TryGetValue((tools, colorful, widgets, toolOnly), out var gbnf))
        {
            gbnf = AnswerGrammar.Build(tools ? _options.Tools!.GrammarRule() : null, colorful, widgets, toolOnly);
            _grammars[(tools, colorful, widgets, toolOnly)] = gbnf;
        }
        return gbnf;
    }

    /// <summary>
    /// Die Antwort bis vor den Block des Elements <paramref name="element"/>, neu in Tokens zerlegt –
    /// oder null, wenn der Block nicht zu finden ist.
    /// </summary>
    private List<int>? WithoutElement(List<int> answer, string element)
    {
        var decoder = _model.CreateDecoder();
        var text = new StringBuilder();
        foreach (var token in answer)
            text.Append(decoder.Add(token));
        var all = text.ToString();
        var fence = all.LastIndexOf("```" + element, StringComparison.OrdinalIgnoreCase);
        if (fence < 0)
            return null;
        // Der Block beginnt am Zeilenanfang (höchstens Leerzeichen davor) – ab dort fällt alles weg.
        var start = fence == 0 ? 0 : all.LastIndexOf('\n', fence - 1) + 1;
        return start == 0 ? [] : [.. _model.Tokenize(all[..start])];
    }

    // Nicht "Farbe" allein – "Welche Farbe hat der Kreis?" ist kein Stilwunsch (Selbsttest 48).
    [System.Text.RegularExpressions.GeneratedRegex(@"\b(bunt\w*|farbig\w*|farbenfroh\w*|farbverl[äa]uf\w*|colou?rful|colou?rs?|mit\s+(vielen\s+)?farben|in\s+farbe|ohne\s+farben?|keine\s+farben?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ColorWishRegex();

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
