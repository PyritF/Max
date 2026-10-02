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
    private ContextWindow _window;
    private readonly Dictionary<(bool Tools, bool Colorful, bool Widgets, bool ToolOnly), string> _grammars = [];

    // Tokens je Nachricht. Für eigene Antworten genau die Tokens, die auch im Cache des Modells stehen –
    // nicht neu zerlegt, damit der nächste Prompt exakt zum Cache passt.
    private readonly Dictionary<(ChatRole Role, string Content), IReadOnlyList<int>> _tokens = [];
    private IReadOnlyList<int>? _systemTokens;
    private (string? Summary, IReadOnlyList<int> Tokens)? _systemWithSummary;
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

    /// <summary>
    /// Nur für den Selbsttest: tut so, als wäre der Kontext kleiner (null = wieder normal) – damit ein langes
    /// Gespräch schon nach ein paar Fragen zu lang wird.
    /// </summary>
    internal void LimitContext(int? budget) =>
        _window = new ContextWindow(budget ?? Math.Max(256, _model.ContextSize - AnswerReserve - _options.ThinkingBudget));

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

        // Was frühere Runden dieser Antwort schon gezeigt haben ("Ich sehe nach." vor einem Aufruf mitten in der
        // Antwort) – die Anzeige hängt alle Runden aneinander, und so kommt die Antwort in den Verlauf.
        var shownBefore = new StringBuilder();
        for (var round = 0; ; round++)
        {
            var toolCall = new ToolCallSlot();
            var allowTools = _options.Tools is not null && round < _options.MaxToolRounds;
            var earlier = shownBefore.ToString();
            await foreach (var chunk in RoundAsync(conversation, allowTools, toolCall, earlier, ct))
            {
                if (!chunk.IsThinking && !chunk.IsTool && !chunk.IsStatus)
                    shownBefore.Append(chunk.Text);
                yield return chunk;
            }
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
    /// <param name="shownBefore">Was frühere Runden dieser Antwort schon gezeigt haben.</param>
    private async IAsyncEnumerable<ReplyChunk> RoundAsync(Conversation conversation, bool allowTools, ToolCallSlot toolCall, string shownBefore,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await FinishCommitAsync();

        // Wird das Gespräch zu lang, notiert Max sich erst den Anfang, der gleich wegfällt.
        if (SummaryCut(conversation) is { } cut)
        {
            yield return new ReplyChunk("Notiere mir den Anfang unseres Gesprächs – er wird zu lang", IsStatus: true);
            await SummarizeAsync(conversation, cut, ct);
        }

        var think = ThinkingEnabled;
        var prompt = BuildPrompt(conversation.Messages, conversation.Summary, conversation.SummarizedCount);
        var head = think ? _thinkingStart! : _assistantStart!;
        var clock = Stopwatch.StartNew();

        var reused = await _model.PrefillAsync(prompt, ct);
        var beforeReply = _model.Checkpoint();
        await _model.AppendAsync(head, ct);

        var splitter = new ThinkSplitter(startInThinking: think);
        if (think)
            yield return new ReplyChunk(_template.ThinkingSeed, IsThinking: true);
        var gate = new ElementGate { HoldFirstFence = allowTools, IsCallLine = allowTools ? _options.Tools!.IsCallLine : null };
        // Was frühere Antworten am Schluss sagten – kommt es wieder, ist es eine Angewohnheit (siehe ClosingFilter).
        // Nach einem Werkzeug-Ergebnis ist die Antwort mit dem Ergebnis fertig – ohne Rückfrage am Ende.
        var closing = new ClosingFilter(conversation.Messages
            .Where(m => m.Role == ChatRole.Assistant && !m.Content.StartsWith("```" + Tools.ToolCall.BlockName, StringComparison.Ordinal))
            .Select(m => ClosingFilter.ClosingParagraph(m.Content)).OfType<string>().TakeLast(12),
            factual: conversation.Messages.LastOrDefault()?.Role == ChatRole.Tool);
        var decoder = _model.CreateDecoder();
        var answerPhase = !think;
        // Anfangs darf das Nachdenken nicht gleich wieder enden – sonst denkt das Modell in der Antwort weiter.
        var colorful = WantsColors(conversation.Messages);
        var widgets = true;
        var codeReleased = false;           // ein Code-Block vom Anfang floss als Text weiter (Werkzeug-Zweig der Grammatik)
        var grammar = Grammar(allowTools, colorful, widgets);
        // Vor dem ersten Werkzeug-Ergebnis dieser Frage: Darf (oder muss) die Antwort mit einem Aufruf beginnen?
        var firstRound = allowTools && conversation.Messages.LastOrDefault()?.Role != ChatRole.Tool;
        if (!think && firstRound && _options.Tools!.RequiredTool(conversation.LastUserMessage?.Content) is { } required)
        {
            LlmEngine.Log($"Die Frage verlangt '{required}' – Antwort beginnt mit dem Aufruf.");
            grammar = Grammar(allowTools, colorful, widgets, toolOnly: true);
        }
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
        int? loopStart = null;                  // dreht sich die Antwort im Kreis: ab hier wiederholt sie sich
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
                    loopStart = LoopStart(shown.ToString());
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
                            await RewindAsync(beforeReply, prompt, head, generated, ct);
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
                        await RewindAsync(beforeReply, prompt, head, generated, ct);
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
                    // auch wirklich – sonst behauptet er es nur und rechnet im Kopf. Ebenso, wenn schon die Frage eins
                    // verlangt ("Schau im Web nach"). Nur vor dem ersten Ergebnis.
                    if (firstRound && (_options.Tools!.IntendedTool(thought.ToString()) ?? _options.Tools.RequiredTool(conversation.LastUserMessage?.Content)) is { } intended)
                    {
                        LlmEngine.Log($"Werkzeug '{intended}' beschlossen – Antwort beginnt mit dem Aufruf.");
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
                        await RewindAsync(beforeReply, prompt, head, generated, ct);
                        released = gate.Drop();
                    }
                    else if (widgets && !codeReleased && WithoutElement(answerAll, closed.Name) is { } keep)
                    {
                        // Auch neu erzeugt kaputt: zurück vor den Block und ohne Elemente weiterschreiben, statt ihn samt
                        // Inhalt wegzulassen (Selbsttest 54: das Ergebnis einer Rechnung als Balken – die Zahl fehlte).
                        gate.Reset();
                        repair?.Dispose();
                        repair = null;
                        LlmEngine.Log($"Element '{closed.Name}' ungültig – die Antwort geht ohne Elemente weiter.");
                        Truncate(generated, generated.Count - answerAll.Count);
                        generated.AddRange(keep);
                        answerAll.Clear();
                        answerAll.AddRange(keep);
                        Truncate(answerTokens, Math.Min(answerTokens.Count, keep.Count));
                        await RewindAsync(beforeReply, prompt, head, generated, ct);
                        widgets = false;
                        grammar = Grammar(allowTools, colorful, widgets);
                        sampler.Dispose();
                        sampler = CreateAnswerSampler(grammar);
                        foreach (var t in answerAll)
                            sampler.Accept(t);
                        decoder = _model.CreateDecoder();
                        continue;
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
                else if (WidgetValidator.IsValid(open.Name, open.Body))
                {
                    rest += gate.Accept();
                }
                else
                {
                    LlmEngine.Log($"Element '{open.Name}' am Ende ungültig, weggelassen: {open.Body.ReplaceLineEndings(" / ")}");
                    rest += gate.Drop();
                }
            }
            rest = closing.Push(rest) + closing.Flush();
            if (rest.Length > 0)
            {
                shown.Append(rest);
                yield return new ReplyChunk(rest);
            }
            // Nie eine leere Antwort (Selbsttest 60: ein kaputter Balken ganz am Ende fiel weg, und nichts blieb übrig).
            // Der Satz steht nur in der Anzeige, nicht im Cache.
            if (toolCall.Call is null && shownBefore.Trim().Length == 0 && shown.ToString().Trim().Length == 0)
            {
                shown.Append(Sorry);
                yield return new ReplyChunk(Sorry);
            }
        }
        finally
        {
            sampler.Dispose();
            repair?.Dispose();
            var seconds = genClock.Elapsed.TotalSeconds;
            LastRun = new GenerationStats(prompt.Count + head.Count, reused, generated.Count, thinkingTokens,
                thinkingTime, firstToken, seconds > 0 ? (generated.Count - 1) / seconds : 0, repairs);
            Finish(beforeReply, head, generated, shownBefore, shown.ToString(), loopStart, toolCall.Call is not null, WantsColors(conversation.Messages));
            beforeReply?.Dispose();
        }
    }

    /// <summary>Lieber ein ehrlicher Satz als eine leere Antwort.</summary>
    internal const string Sorry = "Das wollte mir gerade nicht gelingen. Frag mich gern noch einmal, vielleicht etwas anders.";

    /// <summary>
    /// Den Cache auf Prompt, Kopf und <paramref name="generated"/> bringen, wenn Verworfenes darin steht (ein Element).
    /// Rekurrente Schichten lassen sich nicht kürzen – also zurück auf den Stand vor der Antwort und nur die Antwort
    /// nachrechnen. Den ganzen Verlauf neu zu rechnen dauerte auf der CPU über zehn Minuten (Selbsttest 56).
    /// </summary>
    private async Task RewindAsync(ModelCheckpoint? beforeReply, IReadOnlyList<int> prompt, IReadOnlyList<int> head, List<int> generated, CancellationToken ct)
    {
        if (beforeReply is not null && _model.Restore(beforeReply))
            await _model.AppendAsync([.. head, .. generated], ct);
        else
            await _model.PrefillAsync([.. prompt, .. head, .. generated], ct);
    }

    /// <summary>
    /// Nach der Antwort: Den Cache so hinterlassen, wie der Verlauf beim nächsten Mal aussieht.
    /// Zurück vor die Antwort und sie in Verlaufsform (ohne Denk-Block) nachrechnen – im Hintergrund.
    /// </summary>
    /// <param name="shownBefore">Was frühere Runden dieser Antwort gezeigt haben – es kommt mit in den Verlauf.</param>
    /// <param name="loopStart">Ab hier hat sich die Antwort wiederholt – das kommt nicht in den Verlauf, sonst wird die Schleife zum Vorbild.</param>
    /// <param name="calledTool">Die Runde endete mit einem Werkzeug-Aufruf.</param>
    private void Finish(ModelCheckpoint? beforeReply, IReadOnlyList<int> head, List<int> generated, string shownBefore, string shown, int? loopStart,
        bool calledTool, bool keepColors)
    {
        // So steht die Antwort gleich im Verlauf: alles, was die Anzeige aus allen Runden aneinandergehängt hat.
        var reply = shownBefore + shown;
        if (beforeReply is null)
        {
            // Ohne Zwischenstand bleibt der Kopf (samt Nachdenken) im Cache – dann eben auch im Verlauf (bleibt schnell).
            if (shown.Length > 0 && !calledTool)
                Remember(reply, [.. head, .. generated, .. _assistantEnd!]);
            return;
        }
        if (calledTool)
        {
            // Der Aufruf kommt als eigene Nachricht in den Verlauf (StreamReplyAsync), ein Text davor mit der Antwort der
            // nächsten Runde. Also nur zurück vor die Runde – dann passt der nächste Prompt zum Cache. Selbsttest 58: Mit
            // dem Text davor im Cache rechnete Max zweimal den ganzen Verlauf neu, auf der CPU je acht Minuten.
            _model.Restore(beforeReply);
            return;
        }

        // Im Verlauf steht, was der Nutzer gesehen hat – ohne verworfene Elemente oder abgebrochene Blöcke.
        // Farb-Tags nur, wenn er Farben wollte: Sonst färbt eine zufällig bunte Antwort alle weiteren mit.
        // Antwortmöglichkeiten nach einer Schlussfrage ("Was willst du machen? / - Plaudern / - Arbeiten") nur zeigen,
        // nicht merken: Sonst hängt das Modell ab da an jede Antwort so ein Menü aus Text. Ebenso Code-Blöcke ohne Code
        // und eine Schleife: Was in früheren Antworten steht, macht das Modell nach.
        var kept = shownBefore + (loopStart is { } cut ? WithClosedFence(shown[..cut]) : shown);
        var remembered = ClosingFilter.WithoutPseudoCode(ClosingFilter.WithoutTrailingOptions(keepColors ? kept : ColorTags.Strip(kept)));
        var history = new List<int>([.. _historyStart!, .. _model.Tokenize(remembered), .. _assistantEnd!]);
        if (!_model.Restore(beforeReply))
        {
            if (reply.Length > 0)
                Remember(reply, history);          // Cache ist leer, der nächste Prompt rechnet alles neu
            return;
        }
        if (reply.Length == 0)
            return;

        Remember(reply, history);
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
    /// <param name="summary">Was Max sich vom Anfang des Gesprächs notiert hat – steht dann im System-Prompt.</param>
    /// <param name="skip">So viele Nachrichten vom Anfang deckt die Notiz ab.</param>
    /// <param name="keepStart">Den Verlauf genau so beginnen lassen wie in der letzten Runde (nichts kürzen) – dann passt der Cache.</param>
    public async Task<string> RunTaskAsync(IReadOnlyList<ChatMessage> messages, string instruction, string? grammar, int maxTokens, Func<string, bool>? done,
        CancellationToken ct, string? summary = null, int skip = 0, bool keepStart = false)
    {
        await FinishCommitAsync();
        var prompt = BuildPrompt([.. messages, new ChatMessage(ChatRole.User, instruction, DateTime.Now)], summary, skip, keepStart);
        if (prompt.Count + _assistantStart!.Count + maxTokens >= _model.ContextSize)
            throw new InvalidOperationException("Für diesen Auftrag ist im Kontext kein Platz mehr.");
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
    /// <param name="summary">Die Notiz vom Anfang eines langen Gesprächs – steht am Ende des System-Prompts.</param>
    /// <param name="skip">So viele Nachrichten vom Anfang deckt die Notiz ab; sie gehen nicht mit.</param>
    /// <param name="keepStart">Nicht kürzen, sondern wie in der letzten Runde beginnen (für Aufträge über den Cache).</param>
    internal IReadOnlyList<int> BuildPrompt(IReadOnlyList<ChatMessage> messages, string? summary = null, int skip = 0, bool keepStart = false)
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

        var system = SystemTokens(summary);
        var fixedCost = system.Count + _thinkingStart.Count;
        var first = keepStart
            ? Math.Max(_window.Current(messages), Math.Min(skip, messages.Count - 1))
            : _window.FirstIncluded(messages, fixedCost, m => TokensOf(m).Count, skip);
        // Die letzte Frage des Nutzers bleibt immer – sonst antwortet Max auf ein Werkzeug-Ergebnis ohne Frage.
        var question = LastIndexOf(messages, ChatRole.User);
        if (question >= 0 && first > question)
            first = question;
        var included = messages.Skip(Math.Max(0, first)).ToList();
        if (!keepStart)
            FitTurn(included, fixedCost);

        var prompt = new List<int>(system);
        foreach (var message in included)
            prompt.AddRange(TokensOf(message));
        return prompt;
    }

    /// <summary>Der System-Prompt, bei einem langen Gespräch mit der Notiz vom Anfang dahinter.</summary>
    private IReadOnlyList<int> SystemTokens(string? summary)
    {
        if (summary is null)
            return _systemTokens!;
        if (_systemWithSummary is not { } cached || cached.Summary != summary)
        {
            cached = (summary, _model.Tokenize(_template.Message(ChatRole.System, _systemPrompt + SummarySection(summary))));
            _systemWithSummary = cached;
        }
        return cached.Tokens;
    }

    internal static string SummarySection(string summary) =>
        "\n\n## Früher in diesem Gespräch\n" +
        "Der Anfang dieses Gesprächs passt nicht mehr in dein Gedächtnis. Das hast du dir davon notiert – knüpf daran an, " +
        "wenn der Nutzer darauf zurückkommt:\n" + summary.Trim();

    // ── Lange Gespräche: den Anfang zusammenfassen, statt ihn einfach zu vergessen ──

    /// <summary>Höchstens so lang wird die Notiz vom Anfang.</summary>
    internal const int MaxSummaryTokens = 400;

    internal const string SummaryInstruction = """
        (Interne Aufgabe, nicht Teil des Gesprächs – der Nutzer sieht das nicht.)
        Das Gespräch wird zu lang, der Anfang fällt gleich aus deinem Gedächtnis. Notiere dir, was du brauchst, um später
        daran anzuknüpfen: worum es ging, was der Nutzer wollte, Ergebnisse, Zahlen, Namen, Dateien, Abmachungen und
        offene Fragen. Steht im System-Prompt schon eine Notiz vom Anfang, nimm ihren Inhalt mit auf.
        Höchstens 10 knappe Stichpunkte, je eine Zeile, ohne Einleitung.
        """;

    internal const string SummaryGbnf = """
        root ::= item{1,10}
        item ::= "- " [^\n]{3,220} "\n"
        """;

    /// <summary>
    /// Fällt in dieser Runde vorn etwas weg, das noch nicht zusammengefasst ist? Dann ab welcher Nachricht der Verlauf
    /// danach beginnt – sonst null. (Für die Notiz rechnet das Modell mit etwas Platz.)
    /// </summary>
    private int? SummaryCut(Conversation conversation)
    {
        var messages = conversation.Messages;
        var question = LastIndexOf(messages, ChatRole.User);
        if (question <= conversation.SummarizedCount)
            return null;
        _systemTokens ??= _model.Tokenize(_template.Message(ChatRole.System, _systemPrompt));
        _thinkingStart ??= _model.Tokenize(_template.AssistantStartThinking + _template.ThinkingSeed);
        var fixedCost = SystemTokens(conversation.Summary).Count + _thinkingStart.Count + (conversation.Summary is null ? MaxSummaryTokens : 0);
        var first = Math.Min(_window.Compute(messages, fixedCost, m => TokensOf(m).Count, conversation.SummarizedCount), question);
        return first > conversation.SummarizedCount ? first : null;
    }

    /// <summary>
    /// Lässt das Modell den Anfang zusammenfassen – über den Cache, in dem das Gespräch bis vor die neue Frage noch
    /// ganz steht: Es rechnet nur den Auftrag und die Notiz. Klappt das nicht, gibt es eine schlichte Notiz aus den
    /// Fragen des Nutzers.
    /// </summary>
    private async Task SummarizeAsync(Conversation conversation, int cut, CancellationToken ct)
    {
        var messages = conversation.Messages;
        var question = LastIndexOf(messages, ChatRole.User);
        string? summary = null;
        try
        {
            var text = await RunTaskAsync(messages.Take(question).ToList(), SummaryInstruction, SummaryGbnf, MaxSummaryTokens,
                t => t.Count(c => c == '\n') >= 10, ct, conversation.Summary, conversation.SummarizedCount, keepStart: true);
            var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Length > 4).ToList();
            if (lines.Count > 0)
                summary = string.Join('\n', lines);
        }
        catch (InvalidOperationException e)
        {
            LlmEngine.Log($"Notiz vom Anfang nicht geschrieben ({e.Message}) – nehme die Fragen.");
        }
        summary ??= PlainSummary(conversation, cut);
        LlmEngine.Log($"Anfang des Gesprächs zusammengefasst (bis Nachricht {cut}): {summary.ReplaceLineEndings(" / ")}");
        conversation.Summarize(summary, cut);
    }

    /// <summary>Notfalls: die bisherige Notiz und die Fragen des Nutzers aus dem Teil, der wegfällt.</summary>
    internal static string PlainSummary(Conversation conversation, int cut)
    {
        var lines = (conversation.Summary ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var message in conversation.Messages.Take(cut).Skip(conversation.SummarizedCount).Where(m => m.Role == ChatRole.User))
        {
            var text = string.Join(' ', message.Content.ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            lines.Add("- Der Nutzer fragte: " + (text.Length <= 120 ? text : text[..119] + "…"));
        }
        return string.Join('\n', lines.TakeLast(12));
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

    /// <summary>
    /// Wo sich eine Antwort, die <see cref="IsLooping"/> erkannt hat, zu wiederholen beginnt: beim ersten Vorkommen des
    /// Endstücks, zurück bis zum Zeilenanfang. Davor steht höchstens noch ein angefangener Durchgang.
    /// </summary>
    internal static int LoopStart(string text)
    {
        var first = text.IndexOf(text[^LoopChars..], StringComparison.Ordinal);
        return first <= 0 ? 0 : text.LastIndexOf('\n', first - 1) + 1;
    }

    /// <summary>Ein abgeschnittener Text mit offenem Code-Block bekommt sein schließendes ```.</summary>
    internal static string WithClosedFence(string text)
    {
        var fences = text.Split('\n').Count(l => l.StartsWith("```", StringComparison.Ordinal));
        return fences % 2 == 0 ? text : text.TrimEnd('\n') + "\n```\n";
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
