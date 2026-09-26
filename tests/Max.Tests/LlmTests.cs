using System.Runtime.CompilerServices;
using System.Text;
using Max.Chat;
using Max.Commands;
using Max.Llm;
using Max.Persona;
using Max.Setup;
using Max.Ui;

namespace Max.Tests;

public class ChatTemplateTests
{
    private readonly ChatMlTemplate _template = new();

    [Fact]
    public void Messages_UseChatMl()
    {
        Assert.Equal("<|im_start|>system\nSei Max.<|im_end|>\n", _template.Message(ChatRole.System, "Sei Max."));
        Assert.Equal("<|im_start|>user\nHallo<|im_end|>\n", _template.Message(ChatRole.User, "Hallo"));
    }

    [Fact]
    public void NewAnswer_StartsWithEmptyThinkBlock_HistoryHasNone()
    {
        Assert.Equal("<|im_start|>assistant\n<think>\n\n</think>\n\n", _template.AssistantStart);
        Assert.Equal("<|im_start|>assistant\nGuten Abend.<|im_end|>\n", _template.Message(ChatRole.Assistant, "Guten Abend."));
    }
}

public class ThinkSplitterTests
{
    private static (string Thinking, string Answer) Run(bool startInThinking, params string[] chunks)
    {
        var splitter = new ThinkSplitter(startInThinking);
        var thinking = new StringBuilder();
        var answer = new StringBuilder();
        foreach (var chunk in chunks)
            foreach (var (isThinking, text) in splitter.Push(chunk))
                (isThinking ? thinking : answer).Append(text);
        foreach (var (isThinking, text) in splitter.Flush())
            (isThinking ? thinking : answer).Append(text);
        return (thinking.ToString(), answer.ToString());
    }

    [Fact]
    public void PlainText_IsAnswer() => Assert.Equal(("", "Hallo Welt"), Run(false, "Hallo", " Welt"));

    [Fact]
    public void ThinkBlock_IsSeparated() =>
        Assert.Equal(("grübel grübel", "Antwort"), Run(false, "<think>grübel grübel</think>\n\nAntwort"));

    [Fact]
    public void StartingInThinking_NeedsOnlyTheClosingTag() =>
        Assert.Equal(("hm, mal sehen", "Klar."), Run(true, "hm, mal", " sehen</th", "ink>\n\n", "Klar."));

    [Fact]
    public void Tags_SplitAcrossChunks() =>
        Assert.Equal(("geheim", "vorher Antwort"), Run(false, "vorher <th", "ink>geh", "eim</thi", "nk>Antwort"));

    [Fact]
    public void UnclosedThinkBlock_StaysThinking() => Assert.Equal(("denke und denke", ""), Run(true, "denke", " und denke"));

    [Fact]
    public void LookalikeTag_IsKept() => Assert.Equal(("", "a <thin client"), Run(false, "a <thin", " client"));

    [Fact]
    public void StrayClosingTag_InTheAnswer_IsSwallowed() =>
        Assert.Equal(("", "Entwurf Antwort"), Run(false, "Entwurf</th", "ink> Antwort"));

    [Fact]
    public void AfterThinking_NoFurtherThinkBlock() =>
        Assert.Equal(("x", "Da. Noch was"), Run(true, "x</think>Da. <think>Noch", "</think> was"));

    [Fact]
    public void LeadingBlankLines_OfTheAnswer_AreTrimmed() => Assert.Equal(("x", "Da."), Run(true, "x</think>", "\n", "\nDa."));
}

public class ContextWindowTests
{
    private static List<ChatMessage> Turns(int count)
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < count; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"frage {i}", DateTime.Now));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"antwort {i}", DateTime.Now));
        }
        return messages;
    }

    [Fact]
    public void EverythingFits_StartsAtZero() =>
        Assert.Equal(0, new ContextWindow(1000).FirstIncluded(Turns(3), 100, _ => 10));

    [Fact]
    public void TooLong_DropsOldestTurns_WithHeadroom_AndStartsWithUser()
    {
        var messages = Turns(10);                 // 20 Nachrichten à 10 Tokens + 100 fest = 300
        var first = new ContextWindow(250).FirstIncluded(messages, 100, _ => 10);

        Assert.Equal(ChatRole.User, messages[first].Role);
        Assert.True(100 + (messages.Count - first) * 10 <= 250 * ContextWindow.RefillRatio);
    }

    [Fact]
    public void Start_StaysStable_UntilFullAgain()
    {
        var messages = Turns(10);
        var window = new ContextWindow(250);
        var first = window.FirstIncluded(messages, 100, _ => 10);

        messages.Add(new ChatMessage(ChatRole.User, "noch eine", DateTime.Now));
        Assert.Equal(first, window.FirstIncluded(messages, 100, _ => 10)); // noch Platz → gleicher Anfang = Cache passt
    }

    [Fact]
    public void AfterClear_StartsFromScratch()
    {
        var window = new ContextWindow(250);
        window.FirstIncluded(Turns(10), 100, _ => 10);
        Assert.Equal(0, window.FirstIncluded(Turns(1), 100, _ => 10));
    }

    [Fact]
    public void NewestMessage_AlwaysStays_EvenIfHuge()
    {
        var messages = Turns(2);
        messages.Add(new ChatMessage(ChatRole.User, "riesig", DateTime.Now));
        var first = new ContextWindow(250).FirstIncluded(messages, 100, m => m.Content == "riesig" ? 1000 : 10);
        Assert.Equal(messages.Count - 1, first);
    }
}

public class SystemPromptTests
{
    [Fact]
    public void AllPlaceholders_AreFilled()
    {
        var system = new SystemSnapshot(new DateTime(2026, 9, 25, 21, 14, 0), UserIdentity.Create("alex", "Alex Beispiel"), "Windows 11", 16,
            new HardwareInfo(32L << 30, null), @"C:\Users\alex");
        var prompt = SystemPrompt.Build(system);

        Assert.StartsWith("Du bist Max.", prompt);
        Assert.DoesNotContain("{{", prompt);
        Assert.Contains("Freitag, 25. September 2026", prompt);
        Assert.Contains("21:14", prompt);
        Assert.Contains("Windows 11", prompt);
        Assert.Contains("Nutzer: Alex Beispiel (Vorname: Alex)", prompt);
        Assert.Contains("Du duzt den Nutzer immer", prompt);
        Assert.DoesNotContain("\r", prompt);
    }

    private static readonly SystemSnapshot Snapshot = new(new DateTime(2026, 9, 25, 21, 14, 0), UserIdentity.Create("alex", "Alex Beispiel"), "Windows 11", 16,
        new HardwareInfo(32L << 30, null), @"C:\Users\alex");

    [Theory]
    [InlineData("S", true)]
    [InlineData("M", true)]
    [InlineData("L", false)]
    [InlineData("XL", false)]
    public void SmallTiers_LearnFromExamples_BigOnesFromRules(string tier, bool examples)
    {
        var prompt = SystemPrompt.Build(Snapshot, Parse(tier));
        Assert.Equal(examples, prompt.Contains("Schreib es genau so wie im Beispiel"));
        Assert.Equal(!examples, prompt.Contains("Nie ein Element bei:"));
        Assert.DoesNotContain("{{", prompt);
        Assert.DoesNotContain("Stufe", prompt);
    }

    [Theory]
    [InlineData("S")]
    [InlineData("L")]
    public void EveryTier_KnowsEveryElement(string tier)
    {
        var prompt = SystemPrompt.Build(Snapshot, Parse(tier));
        foreach (var name in Max.Ui.Widgets.WidgetRegistry.Names.Append("frage"))
            Assert.Contains("```" + name, prompt);
        Assert.Contains("{verlauf:", prompt);
    }

    private static Tier Parse(string tier) => Enum.Parse<Tier>(tier);

    [Fact]
    public void ExampleElements_AreValid()
    {
        var prompt = SystemPrompt.Build(Snapshot, Max.Setup.Tier.S);
        var blocks = System.Text.RegularExpressions.Regex.Matches(prompt, "```(\\p{L}+)\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(blocks.Count >= 10);
        foreach (System.Text.RegularExpressions.Match block in blocks)
            Assert.True(Max.Ui.Widgets.WidgetValidator.IsValid(block.Groups[1].Value, block.Groups[2].Value), block.Value);
    }
}

public class GgufInfoTests
{
    [Fact]
    public void ReadsArchitectureLayersAndContext_SkippingArrays()
    {
        var gguf = new GgufBuilder()
            .String("general.architecture", "qwen35")
            .StringArray("tokenizer.ggml.tokens", ["<a>", "<b>", "ü"])
            .Int32Array("tokenizer.ggml.token_type", [1, 2, 3])
            .Float32("qwen35.rope.freq_base", 1e6f)
            .UInt32("qwen35.block_count", 24)
            .UInt32("qwen35.context_length", 262144)
            .Build();

        var info = GgufInfo.Read(new MemoryStream(gguf));
        Assert.Equal(new GgufInfo("qwen35", 24, 262144), info);
    }

    [Fact]
    public void RejectsOtherFiles() =>
        Assert.Throws<InvalidDataException>(() => GgufInfo.Read(new MemoryStream("PK\x03\x04 kein gguf"u8.ToArray())));

    /// <summary>Schreibt einen minimalen GGUF-Kopf (Version 3, ohne Tensoren).</summary>
    private sealed class GgufBuilder
    {
        private readonly MemoryStream _kv = new();
        private readonly BinaryWriter _w;
        private ulong _count;

        public GgufBuilder() => _w = new BinaryWriter(_kv);

        private GgufBuilder Key(string key, uint type)
        {
            WriteString(key);
            _w.Write(type);
            _count++;
            return this;
        }

        private void WriteString(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            _w.Write((ulong)bytes.Length);
            _w.Write(bytes);
        }

        public GgufBuilder String(string key, string value) { Key(key, 8); WriteString(value); return this; }
        public GgufBuilder UInt32(string key, uint value) { Key(key, 4); _w.Write(value); return this; }
        public GgufBuilder Float32(string key, float value) { Key(key, 6); _w.Write(value); return this; }

        public GgufBuilder StringArray(string key, string[] values)
        {
            Key(key, 9); _w.Write(8u); _w.Write((ulong)values.Length);
            foreach (var v in values) WriteString(v);
            return this;
        }

        public GgufBuilder Int32Array(string key, int[] values)
        {
            Key(key, 9); _w.Write(5u); _w.Write((ulong)values.Length);
            foreach (var v in values) _w.Write(v);
            return this;
        }

        public byte[] Build()
        {
            var output = new MemoryStream();
            var w = new BinaryWriter(output);
            w.Write("GGUF"u8);
            w.Write(3u);
            w.Write(0UL);
            w.Write(_count);
            w.Write(_kv.ToArray());
            return output.ToArray();
        }
    }
}

public class GpuOffloadTests
{
    private const long GB = 1_000_000_000;

    [Fact]
    public void NoGpu_NoLayers() => Assert.Equal(0, GpuOffload.Layers(5 * GB, 32, 0));

    [Fact]
    public void FitsCompletely_AllLayers() => Assert.Equal(GpuOffload.All, GpuOffload.Layers(5 * GB, 32, 8 * GB));

    [Fact]
    public void TooBig_Proportional()
    {
        var layers = GpuOffload.Layers(21 * GB, 40, 16 * GB); // 12,8 GB Budget von 21 GB → ~24 Schichten
        Assert.InRange(layers, 23, 25);
    }
}

public class LlmBackendTests
{
    private static readonly ChatMlTemplate Template = new();

    private static LlmBackend Backend(FakeModel model, bool thinking = false, int budget = 100) =>
        new(model, "SYS", new BackendOptions(ThinkingBudget: budget, ThinkingEnabled: () => thinking));

    [Fact]
    public async Task Prompt_IsSystem_History_AssistantStart()
    {
        var model = new FakeModel("Hallo", " zurück.");
        var conversation = Single("Hi");

        var (_, reply) = await Collect(Backend(model).StreamReplyAsync(conversation, CancellationToken.None));

        Assert.Equal("Hallo zurück.", reply);
        var expected = Template.Message(ChatRole.System, "SYS") + Template.Message(ChatRole.User, "Hi") + Template.AssistantStart;
        Assert.Equal(expected, model.Decode(model.PromptBeforeFirstSample));
    }

    [Fact]
    public async Task WarmUp_PrefillsSystemPrompt_WhichTheFirstPromptStartsWith()
    {
        var model = new FakeModel("Hi");
        var backend = Backend(model);
        await backend.WarmUpAsync(CancellationToken.None);

        Assert.NotNull(backend.WarmUpTime);
        await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));
        Assert.Equal(model.Tokenize(Template.Message(ChatRole.System, "SYS")).Count, backend.LastRun!.ReusedTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextPrompt_ContinuesExactlyWhereTheCacheStands(bool thinking)
    {
        // Das Modell erzeugt Tokens, die NICHT dem entsprechen, was Tokenize() aus dem Text machen würde –
        // trotzdem muss der nächste Prompt mit genau dem Cache weitergehen (sonst ist der Cache wertlos).
        var model = new FakeModel("überleg", "</think>", "\n\n", "Ant", "wort", null, "Zweite");
        if (!thinking)
            model = new FakeModel("Ant", "wort", null, "Zweite");
        var backend = Backend(model, thinking);
        var conversation = new Conversation();

        conversation.AddUser("Eins");
        conversation.AddAssistant((await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None))).Answer);
        await backend.CompleteAsync();
        var cacheAfterFirst = model.Cache.ToList();
        conversation.AddUser("Zwei");
        await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));

        Assert.Equal(cacheAfterFirst.Count, backend.LastRun!.ReusedTokens);
        Assert.DoesNotContain("überleg", model.Decode(cacheAfterFirst));
        Assert.DoesNotContain("think>", model.Decode(cacheAfterFirst));
        Assert.EndsWith("<|im_start|>assistant\nAntwort<|im_end|>\n", model.Decode(cacheAfterFirst));
    }

    [Fact]
    public async Task Answer_BansBothThinkTags_Thinking_BansTheEndOnlyAtFirst()
    {
        var thoughts = Enumerable.Repeat<string?>("hm ", LlmBackend.MinThinkingTokens + 2);
        var model = new FakeModel([.. thoughts, "</think>", "Ja."]);
        await Collect(Backend(model, thinking: true).StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.Equal(3, model.SamplerBans.Count);
        Assert.Equal([FakeModel.ThinkClose], model.SamplerBans[0]);                    // die ersten Denk-Tokens
        Assert.Empty(model.SamplerBans[1]);                                            // danach frei
        Assert.Equal([FakeModel.ThinkOpen, FakeModel.ThinkClose], model.SamplerBans[2]); // Antwort
    }

    [Fact]
    public async Task WithoutThinking_TheAnswerAlsoBansThinkTags()
    {
        var model = new FakeModel("Ja.");
        await Collect(Backend(model).StreamReplyAsync(Single("?"), CancellationToken.None));
        Assert.Equal([FakeModel.ThinkOpen, FakeModel.ThinkClose], Assert.Single(model.SamplerBans));
    }

    [Fact]
    public async Task Thinking_IsStreamedSeparately_AndStartsWithOpenThinkBlock()
    {
        var model = new FakeModel("hm", " gut", "</think>", "\n\n", "Klar.");
        var backend = Backend(model, thinking: true);
        var (thought, reply) = await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.Equal(Template.ThinkingSeed + "hm gut", thought);
        Assert.Equal("Klar.", reply);
        Assert.EndsWith(Template.AssistantStartThinking + Template.ThinkingSeed, model.Decode(model.PromptBeforeFirstSample));
        Assert.Equal(3, backend.LastRun!.ThinkingTokens);
    }

    [Fact]
    public async Task ThinkingBudget_ForcesTheEnd_ThenTheAnswerFollows()
    {
        var model = new FakeModel("a", "b", "c", "d", "Antwort");
        var backend = Backend(model, thinking: true, budget: 2);

        var (thought, reply) = await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.StartsWith(Template.ThinkingSeed + "ab", thought);
        Assert.Contains("Genug nachgedacht", thought);
        Assert.Equal("cdAntwort", reply);
        Assert.Equal(2, backend.LastRun!.ThinkingTokens);
    }

    [Fact]
    public async Task Answer_UsesGrammar_Thinking_DoesNot()
    {
        var model = new FakeModel("x", "</think>", "Ja.");
        await Collect(Backend(model, thinking: true).StreamReplyAsync(Single("?"), CancellationToken.None));
        Assert.Equal([false, true], model.SamplerGrammars);
    }

    [Fact]
    public async Task BrokenElement_IsGeneratedAgain_Invisibly()
    {
        var model = new FakeModel("Hier:\n", "```balken\n", "kaputt\n", "```\n", "Schlaf: 8\nArbeit: 8\n", "```\n", "Ende.");
        var backend = Backend(model);

        var (_, reply) = await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.Equal("Hier:\n```balken\nSchlaf: 8\nArbeit: 8\n```\nEnde.", reply);
        Assert.Equal(1, backend.LastRun!.Repairs);
        Assert.DoesNotContain("kaputt", model.Decode(model.Cache));
    }

    [Fact]
    public async Task WhenRestoreFails_EverythingIsRecomputed_AndTheElementDropped()
    {
        var model = new FakeModel("A\n", "```balken\n", "x\n", "```\n", "Ende.") { FailRestore = true };
        var (_, reply) = await Collect(Backend(model).StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.Equal("A\nEnde.", reply);
        Assert.DoesNotContain("x\n", model.Decode(model.Cache));   // Zwischenstände unbrauchbar: der nächste Prompt rechnet neu
    }

    [Fact]
    public async Task RunawayElement_IsAbandoned_AndTheAnswerEnds()
    {
        var endless = Enumerable.Repeat<string?>("| 5% ", 1000);
        var model = new FakeModel(["Vorher\n", "```balken\n", .. endless, "nie"]);
        var backend = Backend(model);

        var (_, reply) = await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.Equal("Vorher\n", reply);
        Assert.DoesNotContain("5%", model.Decode(model.Cache));
    }

    [Fact]
    public async Task RunawayElement_AtTheStart_GivesAnHonestSentence_NotAnEmptyAnswer()
    {
        var model = new FakeModel(["```balken\n", .. Enumerable.Repeat<string?>("| 5% ", 1000)]);
        var (_, reply) = await Collect(Backend(model).StreamReplyAsync(Single("?"), CancellationToken.None));
        Assert.StartsWith("Das wollte mir gerade nicht gelingen.", reply);
    }

    [Fact]
    public async Task Repair_WorksAfterThinking_AtTheStartOfTheAnswer()
    {
        var model = new FakeModel("hm", "</think>", "\n\n", "```balken\n", "kaputt\n", "```\n", "A: 1\nB: 2\n", "```", null);
        var backend = Backend(model, thinking: true);

        var (_, reply) = await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.Equal("```balken\nA: 1\nB: 2\n```", reply);
        Assert.Equal(1, backend.LastRun!.Repairs);
    }

    [Fact]
    public async Task ElementThatStaysBroken_IsDropped()
    {
        var model = new FakeModel("A\n", "```balken\n", "x\n", "```\n", "y\n", "```\n", "z\n", "```\n", "Ende.");
        var backend = Backend(model);

        var (_, reply) = await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.Equal("A\nEnde.", reply);
        Assert.Equal(LlmBackend.MaxRepairs, backend.LastRun!.Repairs);
    }

    [Fact]
    public async Task OfferAtTheEnd_IsNeitherShownNorInTheHistory()
    {
        var model = new FakeModel("Fertig.", "\n\n", "Möchtest du", " mehr?", null, "Gut.");
        var backend = Backend(model);
        var conversation = Single("Hi");

        var (_, reply) = await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));
        Assert.Equal("Fertig.\n\n", reply);

        conversation.AddAssistant(reply);
        conversation.AddUser("Danke");
        await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));
        Assert.DoesNotContain("Möchtest", model.Decode(model.PromptBeforeFirstSample));
    }

    [Fact]
    public async Task RepeatingAnswer_IsStopped()
    {
        var section = "## Abschnitt\nDer Frost wird stärker, doch der Schnee bleibt noch. Die Wärme ist noch warm, aber nicht mehr so sehr.\n\n";
        var model = new FakeModel([.. Enumerable.Repeat<string?>(section, 50)]);
        var (_, reply) = await Collect(Backend(model).StreamReplyAsync(Single("?"), CancellationToken.None));
        Assert.True(reply.Length < 6 * section.Length, $"{reply.Length} Zeichen");
    }

    [Fact]
    public void LongDifferentText_IsNoLoop()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 100; i++)
            text.Append($"Satz Nummer {i} erzählt etwas anderes als die davor.\n");
        Assert.False(LlmBackend.IsLooping(text));
    }

    [Fact]
    public async Task Cancellation_IsPassedThrough()
    {
        using var cts = new CancellationTokenSource();
        var model = new FakeModel("a", "b", "c") { OnSample = i => { if (i == 2) cts.Cancel(); } };
        var received = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in Backend(model).StreamReplyAsync(Single("?"), cts.Token))
                received.Add(chunk.Text);
        });
        Assert.Equal(["a", "b"], received);
    }

    [Fact]
    public void DebugCommand_IsHiddenFromHelp()
    {
        var registry = CommandRegistry.CreateDefault(new DebugCommand(() => []));
        Assert.NotNull(registry.Find("debug"));
        Assert.DoesNotContain(registry.Visible, c => c.Name == "debug");
    }

    private static Conversation Single(string question)
    {
        var conversation = new Conversation();
        conversation.AddUser(question);
        return conversation;
    }

    private static async Task<(string Thinking, string Answer)> Collect(IAsyncEnumerable<ReplyChunk> chunks)
    {
        var thinking = new StringBuilder();
        var answer = new StringBuilder();
        await foreach (var chunk in chunks)
            (chunk.IsThinking ? thinking : answer).Append(chunk.Text);
        return (thinking.ToString(), answer.ToString());
    }
}

/// <summary>
/// Attrappe: ein Token pro Zeichen beim Zerlegen. Beim Erzeugen dagegen ein Token pro Stück aus dem Drehbuch
/// (IDs ab 100.000; null = Ende der Antwort) – so fällt auf, wenn das Backend den Text neu zerlegt statt die echten Tokens zu nehmen.
/// Cache, Zwischenstände und Wiederverwendung verhalten sich wie bei der echten Engine.
/// </summary>
internal sealed class FakeModel(params string?[] script) : ILanguageModel
{
    private const int End = 99_999;
    private readonly Queue<string?> _script = new(script);
    private readonly Dictionary<int, string> _generatedText = [];
    private int _next = 100_000;
    private int _samples;

    public List<int> Cache { get; } = [];
    public IReadOnlyList<int> PromptBeforeFirstSample { get; private set; } = [];
    public List<bool> SamplerGrammars { get; } = [];
    public Action<int>? OnSample { get; init; }
    public bool FailRestore { get; set; }

    public int ContextSize => 100_000;
    public int CachedCount => Cache.Count;

    // Wie beim echten Modell sind die Denk-Tags je ein einzelnes Token.
    public const int ThinkOpen = 0xE001, ThinkClose = 0xE002;
    public List<IReadOnlyCollection<int>> SamplerBans { get; } = [];

    public IReadOnlyList<int> Tokenize(string text) =>
        text.Replace("</think>", ((char)ThinkClose).ToString()).Replace("<think>", ((char)ThinkOpen).ToString()).Select(c => (int)c).ToArray();

    public string Decode(IEnumerable<int> tokens) => string.Concat(tokens.Select(t =>
        _generatedText.TryGetValue(t, out var s) ? s : t == ThinkOpen ? "<think>" : t == ThinkClose ? "</think>" : ((char)t).ToString()));

    public Task<int> PrefillAsync(IReadOnlyList<int> prompt, CancellationToken ct)
    {
        var common = 0;
        while (common < Cache.Count && common < prompt.Count && Cache[common] == prompt[common])
            common++;
        if (common < Cache.Count || common == prompt.Count)
        {
            Cache.Clear();
            common = 0;
        }
        Cache.AddRange(prompt.Skip(common));
        return Task.FromResult(common);
    }

    public async Task AppendAsync(IReadOnlyList<int> tokens, CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        Cache.AddRange(tokens);
    }

    public ITokenSampler CreateSampler(SamplingSettings settings, string? grammar = null, uint? seed = null, IReadOnlyCollection<int>? banned = null)
    {
        SamplerGrammars.Add(grammar is not null);
        SamplerBans.Add(banned ?? []);
        return new Sampler(this, grammar is not null);
    }

    public ITokenDecoder CreateDecoder() => new Decoder(this);

    public bool IsEndOfGeneration(int token) => token == End;

    public ModelCheckpoint? Checkpoint() => new Snapshot(Cache.ToArray());

    public bool Restore(ModelCheckpoint checkpoint)
    {
        Cache.Clear();
        if (FailRestore)
            return false;
        Cache.AddRange(((Snapshot)checkpoint).Tokens);
        return true;
    }

    private int NextToken()
    {
        if (_samples == 0)
            PromptBeforeFirstSample = Cache.ToArray();
        OnSample?.Invoke(_samples);
        _samples++;
        if (!_script.TryDequeue(out var piece) || piece is null)
            return End;
        var token = _next++;
        _generatedText[token] = piece;
        return token;
    }

    private sealed class Sampler(FakeModel model, bool grammar) : ITokenSampler
    {
        public bool HasGrammar => grammar;
        public int Sample() => model.NextToken();
        public void Accept(int token) { }
        public void Dispose() { }
    }

    private sealed class Decoder(FakeModel model) : ITokenDecoder
    {
        public string Add(int token) => model.Decode([token]);
    }

    private sealed class Snapshot(int[] tokens) : ModelCheckpoint
    {
        public int[] Tokens { get; } = tokens;
        public override int TokenCount => Tokens.Length;
    }
}

public class ClosingFilterTests
{
    private static string Run(params string[] chunks)
    {
        var filter = new ClosingFilter();
        return string.Concat(chunks.Select(filter.Push)) + filter.Flush();
    }

    [Fact]
    public void OfferAtTheEnd_IsDropped() =>
        Assert.Equal("Die Antwort.\n\n", Run("Die Antwort.\n\n", "Möch", "test du mehr ", "wissen?\nSag Bescheid!"));

    [Fact]
    public void OfferInTheMiddle_StaysInOrder()
    {
        const string text = "Eins.\n\nWenn du noch Zeit hast, lohnt sich das Museum.\n\nZwei.";
        Assert.Equal(text, Run([.. text.Select(c => c.ToString())]));
    }

    [Theory]
    [InlineData("Ich bin Max.\n\nWas noch?")]
    [InlineData("Ich bin Max.\n\nHast du noch Fragen dazu?")]
    [InlineData("Ich bin Max.\n\nWillst du, dass ich dir zeige, wie das geht? Oder hast du eine andere Frage?")]
    public void FillersSeenInTheSelfTest_AreDropped(string text) => Assert.Equal("Ich bin Max.\n\n", Run(text));

    [Fact]
    public void OnlyAQuestion_Stays() => Assert.Equal("Soll ich das für C# oder Python schreiben?", Run("Soll ich das für C# oder Python schreiben?"));

    [Theory]
    [InlineData("Text.\n\nWenn du Windows nutzt, geht es anders.")]
    [InlineData("Text.\n\n```python\nSoll ich = 1\n```\n")]
    [InlineData("Text.\n\nSollich ist kein Wort.")]
    public void OtherEndings_Stay(string text) => Assert.Equal(text, Run(text));

    [Fact]
    public void OfferInsideTheParagraph_Stays() =>
        Assert.Equal("Text.\nMöchtest du", Run("Text.\nMöchtest du"));
}

public class ElementGateTests
{
    [Fact]
    public void NormalText_FlowsThroughImmediately()
    {
        var gate = new ElementGate();
        Assert.Equal("Hallo ", gate.Push("Hallo "));
        Assert.Equal("Welt\n", gate.Push("Welt\n"));
    }

    [Fact]
    public void CodeBlock_IsReleasedAfterItsFirstLine()
    {
        var gate = new ElementGate();
        Assert.Equal("", gate.Push("```py"));
        Assert.Equal("```python\nx = 1\n", gate.Push("thon\nx = 1\n"));
    }

    [Fact]
    public void Element_IsHeldUntilClosed_ThenAccepted()
    {
        var gate = new ElementGate();
        Assert.Equal("", gate.Push("```balken\nA: 1\n"));
        Assert.True(gate.InElement);
        Assert.Equal("", gate.Push("```\nDanach"));
        Assert.Equal(("balken", "A: 1\n"), gate.Closed);
        Assert.Equal("```balken\nA: 1\n```\nDanach", gate.Accept());
    }

    [Fact]
    public void Dropped_Element_Disappears_TextAfterItStays()
    {
        var gate = new ElementGate();
        gate.Push("```balken\nkaputt\n```\nweiter");
        Assert.Equal("weiter", gate.Drop());
    }

    [Fact]
    public void Knows_WhenAHeaderIsAboutToOpenAnElement()
    {
        var gate = new ElementGate();
        gate.Push("```bal");
        Assert.True(gate.WouldOpenElement("ken\nA"));
        Assert.False(new ElementGate().WouldOpenElement("```python\n"));
    }

    [Fact]
    public void ClosingFenceWithoutNewline_AtTheEnd_IsNotContent()
    {
        var gate = new ElementGate();
        gate.Push("```balken\nA: 1\n```");
        gate.Flush();
        Assert.Equal(("balken", "A: 1\n"), gate.Closed);
        Assert.Equal("```balken\nA: 1\n```", gate.Accept());
    }

    [Fact]
    public void UnclosedElement_IsReportedAtTheEnd()
    {
        var gate = new ElementGate();
        gate.Push("```frage\nFrage: Ja?\n- ja");
        gate.Flush();
        Assert.Equal("frage", gate.Closed?.Name);
    }
}

public class AnswerGrammarTests
{
    [Fact]
    public void Grammar_Knows_AllColors_Fonts_AndElements()
    {
        var gbnf = AnswerGrammar.Build();
        foreach (var color in ColorTags.Names)
            Assert.Contains($"\"{AnswerGrammar.AsciiOnly(color)}\"", gbnf);
        foreach (var font in Max.Ui.Widgets.TitleWidget.BuiltInFonts)
            Assert.Contains($"\"{font}\"", gbnf);
        foreach (var widget in Max.Ui.Widgets.WidgetRegistry.Names)
            Assert.Contains($"\"{widget}\"", gbnf);
        Assert.StartsWith("root ::= ", gbnf);
    }

    [Fact]
    public void Grammar_IsPureAscii_BecauseWindowsWouldMangleUmlauts()
    {
        var gbnf = AnswerGrammar.Build();
        Assert.All(gbnf, c => Assert.True(c < 128, $"Nicht-ASCII: {c}"));
        Assert.Contains("\"gr\\u00fcn\"", gbnf);
        Assert.Contains("\\u00c0-\\u024f", gbnf);
    }

    [Fact]
    public void EveryReferencedRule_IsDefined()
    {
        // Eine kaputte Grammatik lehnt llama.cpp ab – und der fehlende Sampler bringt Max zum Absturz.
        var gbnf = AnswerGrammar.Build();
        var defined = new HashSet<string>();
        var bodies = new List<string>();
        foreach (var line in gbnf.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(" ::= ", 2);
            Assert.Equal(2, parts.Length);
            defined.Add(parts[0]);
            bodies.Add(parts[1]);
        }
        foreach (var body in bodies)
        {
            var bare = System.Text.RegularExpressions.Regex.Replace(body, @"""(\\.|[^""\\])*""|\[(\\.|[^\]\\])*\]|\{\d+(,\d*)?\}", " ");
            foreach (System.Text.RegularExpressions.Match word in System.Text.RegularExpressions.Regex.Matches(bare, @"[^\s()|*+?]+"))
                Assert.True(defined.Contains(word.Value), $"Unbekannt: '{word.Value}' in: {body}");
        }
    }

    [Fact]
    public void Labels_AreShortAndWithoutColumns_UnitsWithoutNumbers()
    {
        var gbnf = AnswerGrammar.Build();
        Assert.Contains("label ::= [^-:|\\n\\t`{ ] ( [^:|\\n\\t`{ ] | \" \" [^:|\\n\\t`{ ] ){0,24}", gbnf);
        Assert.Contains("plain ::= [^{}`]", gbnf);
        Assert.Contains("root ::= item* ( \"```\" widget item* )? ( \"```\" w-frage [ \\n]* )?", gbnf);   // ein Element, Menü nur am Ende
        Assert.DoesNotContain("w-frage |", gbnf.Split("widget ::= ")[1].Split('\n')[0]);   // "Wort}" statt "{/verlauf}" geht nicht
        Assert.Contains("unit ::= ( [%\\u20ac$\\u00b0] | \" \" [^0-9:", gbnf);
    }

    [Fact]
    public void AsciiOnly_EscapesEverythingElse() =>
        Assert.Equal("gr\\u00fcn \\u024f \\U0001f600", AnswerGrammar.AsciiOnly("grün ɏ 😀"));

    [Fact]
    public void CodeLanguages_NeverCollideWithElements() =>
        Assert.DoesNotContain(AnswerGrammar.CodeLanguages.Concat(AnswerGrammar.PlainLanguages), l => Max.Ui.Widgets.WidgetValidator.IsElement(l));

    [Fact]
    public void EveryRule_IsDefined()
    {
        var gbnf = AnswerGrammar.Build();
        var defined = gbnf.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l[..l.IndexOf(" ::=", StringComparison.Ordinal)]).ToHashSet();
        var withoutStrings = System.Text.RegularExpressions.Regex.Replace(gbnf, "\"(\\\\.|[^\"\\\\])*\"|\\[(\\\\.|[^\\]\\\\])*\\]", " ");
        var used = System.Text.RegularExpressions.Regex.Matches(withoutStrings, @"(?<![\w-])[a-z][a-z-]*(?![\w-]*\s*::=)").Select(m => m.Value);
        Assert.All(used, name => Assert.Contains(name, defined));
    }
}
