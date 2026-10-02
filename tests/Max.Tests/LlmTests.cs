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
    public void Minimum_SkipsWhatIsAlreadySummarized_AndComputeDoesNotRemember()
    {
        var messages = Turns(3);
        var window = new ContextWindow(1000);
        Assert.Equal(4, window.Compute(messages, 100, _ => 10, minimum: 4));
        Assert.Equal(0, window.FirstIncluded(messages, 100, _ => 10));      // Compute hat sich nichts gemerkt
        Assert.Equal(4, window.FirstIncluded(messages, 100, _ => 10, minimum: 4));
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

    internal static readonly SystemSnapshot Snapshot = new(new DateTime(2026, 9, 25, 21, 14, 0), UserIdentity.Create("alex", "Alex Beispiel"), "Windows 11", 16,
        new HardwareInfo(32L << 30, null), @"C:\Users\alex");

    [Fact]
    public void Prompt_DescribesEveryElement_WithoutPlaceholders()
    {
        var prompt = SystemPrompt.Build(Snapshot);
        foreach (var name in Max.Ui.Widgets.WidgetRegistry.Names.Append("frage"))
            Assert.Contains("```" + name, prompt);
        Assert.Contains("{verlauf:", prompt);
        Assert.Contains("Nie ein Element bei:", prompt);
        Assert.DoesNotContain("{{", prompt);
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
    public void SmallCard_KeepsRoomForTheContext()
    {
        var layers = GpuOffload.Layers(5_700_000_000, 32, 4 * GB); // 4 GB − 1,2 GB Reserve → gut die Hälfte
        Assert.InRange(layers, 14, 16);
    }

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
        await backend.CompleteAsync();      // die Antwort wird im Hintergrund nachgerechnet – erst danach den Cache lesen
        Assert.DoesNotContain("kaputt", model.Decode(model.Cache));
    }

    [Fact]
    public async Task WhenRestoreFails_EverythingIsRecomputed_AndTheElementDropped()
    {
        var model = new FakeModel("A\n", "```balken\n", "x\n", "```\n", "Ende.") { FailRestore = true };
        var backend = Backend(model);
        var (_, reply) = await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.Equal("A\nEnde.", reply);
        await backend.CompleteAsync();
        Assert.DoesNotContain("x\n", model.Decode(model.Cache));   // Zwischenstände unbrauchbar: der nächste Prompt rechnet neu
    }

    [Fact]
    public async Task RunawayElement_IsDropped_AndTheAnswerGoesOnWithoutIt()
    {
        var endless = Enumerable.Repeat<string?>("| 5% ", 1300);
        var model = new FakeModel(["Vorher\n", "```balken\n", .. endless, "nie"]);
        var backend = Backend(model);

        var (_, reply) = await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));

        // Das Drehbuch schreibt danach einfach weiter – das echte Modell setzt neu an, ohne Elemente.
        Assert.StartsWith("Vorher\n| 5% ", reply);
        Assert.DoesNotContain("```balken", reply);
        await backend.CompleteAsync();
        Assert.DoesNotContain("```balken", model.Decode(model.Cache));
    }

    [Fact]
    public async Task ElementGoingInCircles_IsDroppedEarly_AndTheAnswerGoesOn()
    {
        var circle = Enumerable.Repeat<string?>("Immer dieselbe Zeile, immer wieder und wieder.\n", 9);
        var model = new FakeModel(["Vorher\n", "```kasten\n", .. circle, "Danach.", null]);
        var backend = Backend(model);

        var (_, reply) = await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.Equal("Vorher\nDanach.", reply);
        Assert.Equal(1, backend.LastRun!.Repairs);
        await backend.CompleteAsync();
        Assert.DoesNotContain("Immer dieselbe", model.Decode(model.Cache));
    }

    [Fact]
    public async Task RunawayElement_Twice_GivesAnHonestSentence_NotAnEmptyAnswer()
    {
        var circle = Enumerable.Repeat<string?>("Immer dieselbe Zeile, immer wieder und wieder.\n", 9).ToArray();
        var model = new FakeModel(["```kasten\n", .. circle, "```kasten\n", .. circle]);
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
    public async Task ElementThatStaysBroken_IsDropped_AndTheAnswerGoesOnWithoutElements()
    {
        // Selbsttest 54: das Ergebnis einer Rechnung dreimal als ungültiger Balken – weggelassen fehlte die Zahl.
        // Jetzt geht es vor dem Block ohne Elemente weiter: Das Modell schreibt die Antwort als Text.
        var model = new FakeModel("A\n", "```balken\n", "x\n", "```\n", "y\n", "```\n", "z\n", "```\n", "Ende.");
        var backend = Backend(model);

        var (_, reply) = await Collect(backend.StreamReplyAsync(Single("?"), CancellationToken.None));

        Assert.Equal("A\nEnde.", reply);
        Assert.Equal(LlmBackend.MaxRepairs, backend.LastRun!.Repairs);
        Assert.Contains("widget", model.SamplerGrammarTexts[0]!);
        Assert.DoesNotContain("\"```\" widget", model.SamplerGrammarTexts[^1]!);     // danach keine Elemente mehr
        await backend.CompleteAsync();
        Assert.DoesNotContain("```balken", model.Decode(model.Cache));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DroppingAnElement_RecomputesOnlyTheAnswer_NotTheWholeConversation(bool runaway)
    {
        // Selbsttest 56: Zurück vor das Element hieß, den ganzen Verlauf neu zu rechnen – auf der CPU über zehn Minuten.
        string?[] script = runaway
            ? ["A\n", "```kasten\n", .. Enumerable.Repeat<string?>("Immer dieselbe Zeile, immer wieder und wieder.\n", 9), "Ende.", null]
            : ["A\n", "```balken\n", "x\n", "```\n", "y\n", "```\n", "z\n", "```\n", "Ende.", null];
        var computedBeforeAnswer = -1;
        FakeModel model = null!;
        model = new FakeModel(script) { OnSample = i => { if (i == 0) computedBeforeAnswer = model.ComputedTokens; } };
        var backend = Backend(model);
        var conversation = Single("Erste Frage");
        conversation.AddAssistant("Eine frühere Antwort, die schon im Cache steht.");
        conversation.AddUser("?");

        var (_, reply) = await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));

        Assert.Equal("A\nEnde.", reply);
        Assert.True(computedBeforeAnswer > 0);
        Assert.Equal(computedBeforeAnswer, model.ComputedTokens);
        await backend.CompleteAsync();
        Assert.EndsWith("A\nEnde." + Template.AssistantEnd, model.Decode(model.Cache));
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
        var prompt = model.Decode(model.LastPromptBeforeSampler);
        Assert.Contains("Danke", prompt);
        Assert.DoesNotContain("Möchtest", prompt);
    }

    [Theory]
    [InlineData("Wie geht's?", false)]
    [InlineData("Schreib ab jetzt alles schön bunt.", true)]
    public async Task ColorTags_StayInTheHistory_OnlyWhenColorsWereWanted(string question, bool keep)
    {
        var model = new FakeModel("{verlauf:grau-blau}Hallo{/verlauf} du.", null, "Gut.");
        var backend = Backend(model);
        var conversation = Single(question);

        var (_, reply) = await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));
        Assert.Equal("{verlauf:grau-blau}Hallo{/verlauf} du.", reply);   // angezeigt wird, was das Modell schrieb

        conversation.AddAssistant(reply);
        conversation.AddUser("Und?");
        await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));
        var prompt = model.Decode(model.LastPromptBeforeSampler);
        Assert.Contains(keep ? "{verlauf:grau-blau}Hallo{/verlauf} du." : Template.HistoryStart + "Hallo du.", prompt);
        Assert.Equal(backend.LastRun!.PromptTokens - Tokens(model, "<|im_start|>user\nUnd?<|im_end|>\n") - Tokens(model, Template.AssistantStart), backend.LastRun.ReusedTokens);
    }

    private static int Tokens(FakeModel model, string text) => model.Tokenize(text).Count;

    [Fact]
    public async Task RepeatingAnswer_IsStopped()
    {
        var section = "## Abschnitt\nDer Frost wird stärker, doch der Schnee bleibt noch. Die Wärme ist noch warm, aber nicht mehr so sehr.\n\n";
        var model = new FakeModel([.. Enumerable.Repeat<string?>(section, 50)]);
        var (_, reply) = await Collect(Backend(model).StreamReplyAsync(Single("?"), CancellationToken.None));
        Assert.True(reply.Length < 6 * section.Length, $"{reply.Length} Zeichen");
    }

    [Fact]
    public async Task RepeatingSentences_WithoutLineBreaks_AreStopped()
    {
        var sentence = "Der Herbst ist ein feiner, goldener Tag, der sich in einem dunklen, blauen Farbverlauf ausbreitet. ";
        var model = new FakeModel([.. Enumerable.Repeat<string?>(sentence, 50)]);
        var (_, reply) = await Collect(Backend(model).StreamReplyAsync(Single("?"), CancellationToken.None));
        Assert.True(reply.Length < 8 * sentence.Length, $"{reply.Length} Zeichen");
    }

    [Fact]
    public async Task RepeatingAnswer_OnlyTheStartGoesIntoTheHistory()
    {
        // Selbsttest 56: Die Schleife aus leeren ```bash-Blöcken stand im Verlauf – danach hatte jede Antwort einen ```bash-Block.
        var line = "Immer wieder dieselbe Zeile, Wort für Wort, ohne dass sie je endet.\n";
        var model = new FakeModel(["Vorher.\n", .. Enumerable.Repeat<string?>(line, 30), null, "Gut."]);
        var backend = Backend(model);
        var conversation = Single("?");

        var (_, reply) = await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));
        Assert.Contains(line + line, reply);                   // gezeigt ist gezeigt

        conversation.AddAssistant(reply);
        conversation.AddUser("Und?");
        await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));
        var prompt = model.Decode(model.LastPromptBeforeSampler);
        Assert.Contains(Template.HistoryStart + "Vorher.\n" + Template.AssistantEnd, prompt);
        Assert.DoesNotContain("Immer wieder", prompt);
    }

    [Fact]
    public async Task PseudoCodeBlock_IsShown_ButNotRemembered()
    {
        var model = new FakeModel("Der Kreis ist **rot**.\n\n", "```bash\n", "Farbe: rot\n", "Objekt: Kreis\n", "```\n", "\nKräftig.", null, "Gut.");
        var backend = Backend(model);
        var conversation = Single("Welche Farbe?");

        var (_, reply) = await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));
        Assert.Contains("Farbe: rot", reply);

        conversation.AddAssistant(reply);
        conversation.AddUser("Und?");
        await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));
        var prompt = model.Decode(model.LastPromptBeforeSampler);
        Assert.Contains(Template.HistoryStart + "Der Kreis ist **rot**.\n\nKräftig." + Template.AssistantEnd, prompt);
        Assert.Equal(backend.LastRun!.PromptTokens - Tokens(model, "<|im_start|>user\nUnd?<|im_end|>\n") - Tokens(model, Template.AssistantStart), backend.LastRun.ReusedTokens);
    }

    [Theory]
    [InlineData("Intro\n", "```bash\n")]
    [InlineData("Ein Absatz davor.\n\n", "Dieselbe Zeile noch einmal und noch einmal.\n")]
    [InlineData("Davor. ", "Ein Satz, der sich ohne Zeilenumbruch immer wiederholt. ")]
    public void LoopStart_IsWhereTheRepetitionBegins(string before, string loop)
    {
        var text = before + string.Concat(Enumerable.Repeat(loop, 60));
        Assert.True(LlmBackend.IsLooping(text));
        var start = LlmBackend.LoopStart(text);
        // Schleifen in einer Zeile: zurück bis zum Zeilenanfang – also samt dem Satz davor.
        Assert.Equal(before.EndsWith('\n') ? before.Length : 0, start);
    }

    [Theory]
    [InlineData("Text\n```bash\nls\n", "Text\n```bash\nls\n```\n")]
    [InlineData("Text\n```bash\nls\n```\nmehr\n", "Text\n```bash\nls\n```\nmehr\n")]
    public void CutText_GetsItsCodeBlockClosed(string text, string expected) =>
        Assert.Equal(expected, LlmBackend.WithClosedFence(text));

    [Fact]
    public void LongDifferentText_IsNoLoop()
    {
        var text = new StringBuilder();
        for (var i = 0; i < 100; i++)
            text.Append($"Satz Nummer {i} erzählt etwas anderes als die davor.\n");
        Assert.False(LlmBackend.IsLooping(text.ToString()));
    }

    [Fact]
    public async Task Cancellation_IsPassedThrough()
    {
        using var cts = new CancellationTokenSource();
        // Buchstaben, mit denen keine Floskel beginnt ("a" könnte "Aber" werden – das hielte der Floskel-Filter kurz zurück).
        var model = new FakeModel("x", "y", "z") { OnSample = i => { if (i == 2) cts.Cancel(); } };
        var received = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in Backend(model).StreamReplyAsync(Single("?"), cts.Token))
                received.Add(chunk.Text);
        });
        Assert.Equal(["x", "y"], received);
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

    /// <summary>Der Cache, als zuletzt ein Sampler erzeugt wurde – bei der zweiten Antwort also deren Prompt.</summary>
    public IReadOnlyList<int> LastPromptBeforeSampler { get; private set; } = [];
    public List<bool> SamplerGrammars { get; } = [];
    public List<string?> SamplerGrammarTexts { get; } = [];
    public Action<int>? OnSample { get; init; }
    public bool FailRestore { get; set; }

    public int ContextSize { get; init; } = 100_000;
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
        ComputedTokens += prompt.Count - common;
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
        SamplerGrammarTexts.Add(grammar);
        SamplerBans.Add(banned ?? []);
        LastPromptBeforeSampler = Cache.ToArray();
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

    /// <summary>Wie viele Tokens insgesamt gerechnet wurden – zeigt, ob der gespeicherte Stand Arbeit gespart hat.</summary>
    public int ComputedTokens { get; private set; }

    public bool SaveState(string path)
    {
        File.WriteAllText(path, string.Join(',', Cache));
        return true;
    }

    public bool LoadState(string path, IReadOnlyList<int> tokens)
    {
        Cache.Clear();
        var saved = File.ReadAllText(path).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        if (!saved.SequenceEqual(tokens))
            return false;
        Cache.AddRange(saved);
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

public class PromptCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "max-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private const string Fixed = "Du bist Max.\nViele feste Regeln.\n";

    private LlmBackend Backend(FakeModel model, string time, string identity = "modell-a", string? fixedPart = null)
    {
        var prompt = (fixedPart ?? Fixed) + $"- Es ist {time} Uhr.";
        return new LlmBackend(model, prompt, new BackendOptions(
            ThinkingEnabled: () => false,
            PromptCache: new PromptCache(_dir, identity),
            StablePromptLength: (fixedPart ?? Fixed).Length));
    }

    [Fact]
    public async Task SecondStart_OnlyComputesTheVariableEnd()
    {
        var first = new FakeModel();
        var backend = Backend(first, "09:00");
        await backend.WarmUpAsync(CancellationToken.None);
        Assert.False(backend.WarmUpFromCache);

        var second = new FakeModel();
        backend = Backend(second, "21:30");                   // andere Uhrzeit – der feste Teil passt trotzdem
        await backend.WarmUpAsync(CancellationToken.None);
        Assert.True(backend.WarmUpFromCache);
        Assert.True(second.ComputedTokens < first.ComputedTokens / 2, $"{second.ComputedTokens} von {first.ComputedTokens}");
        Assert.EndsWith("- Es ist 21:30 Uhr.<|im_end|>\n", second.Decode(second.Cache));
    }

    [Theory]
    [InlineData("modell-b", null)]
    [InlineData("modell-a", "Du bist Max.\nNeue Regeln.\n")]
    public async Task OtherModelOrPrompt_ComputesEverythingAgain(string identity, string? fixedPart)
    {
        await Backend(new FakeModel(), "09:00").WarmUpAsync(CancellationToken.None);

        var model = new FakeModel();
        var backend = Backend(model, "09:00", identity, fixedPart);
        await backend.WarmUpAsync(CancellationToken.None);
        Assert.False(backend.WarmUpFromCache);

        // … und der neue Stand ist danach gespeichert.
        var again = Backend(new FakeModel(), "10:00", identity, fixedPart);
        await again.WarmUpAsync(CancellationToken.None);
        Assert.True(again.WarmUpFromCache);
    }

    [Fact]
    public async Task BrokenFile_IsComputedAgain_WithoutCrash()
    {
        await Backend(new FakeModel(), "09:00").WarmUpAsync(CancellationToken.None);
        File.WriteAllText(Path.Combine(_dir, "prompt.state"), "kaputt");

        var model = new FakeModel();
        var backend = Backend(model, "09:00");
        await backend.WarmUpAsync(CancellationToken.None);
        Assert.False(backend.WarmUpFromCache);
        Assert.EndsWith("- Es ist 09:00 Uhr.<|im_end|>\n", model.Decode(model.Cache));
    }

    [Fact]
    public void FixedPart_EndsBeforeTheFirstPlaceholderLine()
    {
        var prompt = SystemPrompt.BuildParts(SystemPromptTests.Snapshot);
        Assert.True(prompt.StableLength > prompt.Text.Length / 2);
        var rest = prompt.Text[prompt.StableLength..];
        Assert.StartsWith("- Heute ist", rest);
        Assert.DoesNotContain("21:14", prompt.Text[..prompt.StableLength]);
        Assert.DoesNotContain("Alex", prompt.Text[..prompt.StableLength]);
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

    private static string RunFactual(params string[] chunks)
    {
        var filter = new ClosingFilter(factual: true);
        return string.Concat(chunks.Select(filter.Push)) + filter.Flush();
    }

    private static string[] Chars(string text) => [.. text.Select(c => c.ToString())];

    [Theory]
    [InlineData("Die Antwort.\n\nUnd hast du noch Fragen dazu?")]
    [InlineData("Die Antwort.\n\nAber falls du noch etwas brauchst, sag Bescheid.")]
    [InlineData("Die Antwort.\n\nAlso, möchtest du mehr wissen?")]
    public void OfferAfterAConjunction_IsDropped(string text)
    {
        Assert.Equal("Die Antwort.\n\n", Run(text));
        Assert.Equal("Die Antwort.\n\n", Run(Chars(text)));
    }

    // Selbsttest 57: Nach fast jedem Werkzeug-Ergebnis eine Rückfrage – "Und morgen ist Samstag, oder?".
    [Theory]
    [InlineData("Und hast du das Bild selbst gemacht oder ist es ein Testbild?")]
    [InlineData("Und morgen ist Samstag, oder?")]
    [InlineData("Bist du selbst in Wien, oder eher in Graz oder Linz?")]
    [InlineData("Was hast du morgen vor?")]
    [InlineData("{verlauf:türkis-blau}Und bei dir? Hast du schon Herbstfeeling?{/verlauf}")]
    [InlineData("**Und du?** ☕")]
    [InlineData("Und was ist dein Projekt?\n- Konsole\n- Web")]
    public void AfterAToolResult_AClosingQuestion_IsDropped(string question)
    {
        Assert.Equal("Das Ergebnis.\n\n", RunFactual("Das Ergebnis.\n\n", question));
        Assert.Equal("Das Ergebnis.\n\n", RunFactual(Chars("Das Ergebnis.\n\n" + question)));
    }

    [Theory]
    [InlineData("Das Ergebnis.\n\nUnd warum nicht grün? Weil das Auge Blau stärker wahrnimmt.")]       // Inhalt, keine Rückfrage
    [InlineData("Das Ergebnis.\n\nWas heißt das? Die Kaution ist fällig.\n\nMehr dazu im Vertrag.")]
    [InlineData("Welche Datei meinst du?")]                                                             // die ganze Antwort
    [InlineData("Das Ergebnis.\n\nWie du siehst, ist alles da.")]
    [InlineData("Das Ergebnis.\n\n```python\nprint(1)\n```\n\nWas danach kommt, steht im Code.")]
    public void AfterAToolResult_ContentStays(string text)
    {
        Assert.Equal(text, RunFactual(text));
        Assert.Equal(text, RunFactual(Chars(text)));
    }

    [Fact]
    public void AfterAToolResult_RealAnswerFromSelfTest57_KeepsEverythingButTheQuestion()
    {
        const string answer = "Der Gesamtbetrag auf der Rechnung beträgt **152 Euro**.\n\nDie Positionen:\n- Inspektion Fahrrad: 89 €\n"
            + "- Neue Bremsbeläge: 38 €\n- Kette: 25 €\n\nZahlbar innerhalb von 14 Tagen.\n\n";
        const string question = "Und war das eine normale Wartung oder gab es noch etwas Besonderes?";
        Assert.Equal(answer, RunFactual(Chars(answer + question)));
        Assert.Equal(answer + question, Run(Chars(answer + question)));      // ohne Werkzeug bleibt sie
    }

    [Fact]
    public void AfterAToolResult_ALongParagraphStartingLikeAQuestion_FlowsOn()
    {
        var filter = new ClosingFilter(factual: true);
        var shown = filter.Push("Das Ergebnis.\n\nWas die Kosten angeht: ");
        shown += filter.Push(string.Concat(Enumerable.Repeat("Miete, Nebenkosten und Strom kommen dazu, ", 8)));
        Assert.Contains("Was die Kosten angeht", shown);         // nicht erst am Ende des Absatzes
    }

    [Fact]
    public void InSmallTalk_ACounterQuestion_Stays() =>
        Assert.Equal("Alles bestens.\n\nUnd bei dir?", Run("Alles bestens.\n\n", "Und bei dir?"));

    // Selbsttest 56: ab der Mitte in jeder Antwort ein ```bash-Block mit "Name: Wert" – Selbsttest 55: "# Beispiel".
    [Theory]
    [InlineData("Der Kreis ist **rot**.\n\n```bash\nFarbe: rot\nObjekt: Kreis\nQuelle: testbild.png\n```\n\nAlles klar?", "Der Kreis ist **rot**.\n\nAlles klar?")]
    [InlineData("Die Kaution: **2.380 €**.\n\n```bash\n§ 17 Mietsicherheit: 2.380 € (in 3 Raten)\n```\n\nSeite 18.", "Die Kaution: **2.380 €**.\n\nSeite 18.")]
    [InlineData("**152 Euro**.\n```bash\nRECHNUNG: 2026-118\nDatum: 14.09.2026\nGesamt: 152 € (Zahlbar in 14 Tagen)\n```\nVon der Werkstatt.", "**152 Euro**.\nVon der Werkstatt.")]
    [InlineData("Auf dem Bild steht **MAX 42**.\n\n```bash\nText: \"MAX 42\"\n```", "Auf dem Bild steht **MAX 42**.\n")]
    [InlineData("Morgen bedeckt.\n```python\n# Beispiel: Wien\n```", "Morgen bedeckt.")]
    [InlineData("```bash\nPfad: /home/a/b.pdf\n```\n\nDa liegt sie.", "Da liegt sie.")]
    public void PseudoCode_IsLeftOutOfTheHistory(string answer, string remembered) =>
        Assert.Equal(remembered, ClosingFilter.WithoutPseudoCode(answer));

    [Theory]
    [InlineData("So:\n```bash\nls -la\n```")]
    [InlineData("So:\n```bash\n# alles zeigen\nls -la\n```")]
    [InlineData("So:\n```python\nname: str = \"Max\"\n```")]                // Zuweisung mit Typ ist Code
    [InlineData("So:\n```yaml\nFarbe: rot\n```")]                            // in YAML ist das Code
    [InlineData("So:\n```text\nFarbe: rot\n```")]
    [InlineData("```bash\nOrdner: /home/a\n---\n- .git/\n- src/\n```")]
    [InlineData("```bash\nFarbe: rot\n```")]                                 // nur der Block: dann lieber alles behalten
    public void RealCode_StaysInTheHistory(string answer) =>
        Assert.Equal(answer, ClosingFilter.WithoutPseudoCode(answer));

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

    [Theory]
    [InlineData("Die Blätter färben sich.\n\nPasst das zu deinem Herbst?")]
    [InlineData("Die Blätter färben sich.\n\nWas dich am meisten interessiert?")]
    [InlineData("Die Blätter färben sich.\n\n{verlauf:gelb-rot}Klingt das nach dir?{/verlauf}")]
    // Selbsttest 50:
    [InlineData("Die Blätter färben sich.\n\nNoch etwas dazu?")]
    [InlineData("Die Blätter färben sich.\n\nNoch eine Frage dazu?")]
    [InlineData("Die Blätter färben sich.\n\nGibt es etwas, das du berechnen lassen möchtest?")]
    [InlineData("Die Blätter färben sich.\n\nHast du spezielle Anforderungen an deine Projektstruktur?")]
    public void CheckQuestionsAtTheEnd_AreDropped(string text) => Assert.Equal("Die Blätter färben sich.\n\n", Run([.. text.Select(c => c.ToString())]));

    [Theory]
    [InlineData("Der Regen ticktackt.\n\nUnd bei dir? Bleibst du drinnen?")]
    [InlineData("Die Liste ist lang.\n\nWas davon teuer ist, steht dabei.")]
    [InlineData("Die Liste ist lang.\n\nPasst das nicht, nimm die andere.")]
    [InlineData("Die Liste ist lang.\n\nNoch etwas: Die Preise gelten nur bis Freitag.")]
    public void RealQuestionsAndStatements_Stay(string text) => Assert.Equal(text, Run([.. text.Select(c => c.ToString())]));

    [Theory]
    // Selbsttest 51: Ab der Mitte hängte das Modell an jede Antwort eine Linie – die Floskel davor blieb stehen.
    [InlineData("Die Kaution beträgt 2.380 €.\n\nMöchtest du die anderen Klauseln sehen?\n---", "Die Kaution beträgt 2.380 €.\n\n")]
    [InlineData("Die Kaution beträgt 2.380 €.\n\nMöchtest du mehr?\n---\n", "Die Kaution beträgt 2.380 €.\n\n")]
    [InlineData("Auf dem Bild steht **MAX 42**.\n\nDas war es.\n---", "Auf dem Bild steht **MAX 42**.\n\nDas war es.\n")]
    [InlineData("Text.\n\n***\n", "Text.\n\n")]
    [InlineData("Wien hat 2 Millionen Einwohner.\n\nWillst du mehr Details?\n---\nQuelle: example.org", "Wien hat 2 Millionen Einwohner.\n\nQuelle: example.org")]
    // Selbsttest 54: die Linie vor jeder Schlussbemerkung – Linien fallen ganz weg.
    [InlineData("Die Kaution beträgt 2.380 €.\n\n---\n\nSeite 18, § 17.", "Die Kaution beträgt 2.380 €.\n\nSeite 18, § 17.")]
    [InlineData("Eins.\n---\nZwei.", "Eins.\nZwei.")]
    [InlineData("Wien hat 2 Millionen Einwohner.\n\nMöchtest du mehr?\n\nQuelle: example.org", "Wien hat 2 Millionen Einwohner.\n\nQuelle: example.org")]
    public void TrailingRules_AreDropped_AndTheOfferBeforeThem(string text, string expected)
    {
        Assert.Equal(expected, Run(text));
        Assert.Equal(expected, Run([.. text.Select(c => c.ToString())]));
    }

    [Theory]
    [InlineData("Zutaten:\n- Mehl\n- Milch\n- Eier")]
    [InlineData("**Wichtig** zuerst.\nDann der Rest.")]
    [InlineData("Text.\n\n```yaml\n---\nname: max\n```\n")]
    [InlineData("Text.\n\n- - - ist auch eine Linie, aber hier geht es weiter.")]
    public void RulesAndListsInTheMiddle_Stay(string text)
    {
        Assert.Equal(text, Run(text));
        Assert.Equal(text, Run([.. text.Select(c => c.ToString())]));
    }

    private const string EarlierClosing = "Und schwarz getrunken? Ich frage mich, ob das dir hilft, die Zahlen besser zu behalten.";

    private static string RunLearned(params string[] chunks)
    {
        var filter = new ClosingFilter([EarlierClosing]);
        return string.Concat(chunks.Select(filter.Push)) + filter.Flush();
    }

    [Theory]
    // Selbsttest 53: derselbe Schluss hinter jeder Antwort – ab dem zweiten Mal fällt er weg.
    [InlineData("Die Kaution beträgt 2.380 €.\n\nUnd schwarz getrunken? Ich frage mich, ob das dir hilft, die Fakten besser zu behalten.", "Die Kaution beträgt 2.380 €.\n\n")]
    [InlineData("Der Kreis ist **rot**.\n\n{verlauf:grau-blau}Und schwarz getrunken?{/verlauf} Ich frage mich, ob das hilft.", "Der Kreis ist **rot**.\n\n")]
    [InlineData("Der Kreis ist rot.\n\nUnd schwarz getrunken? Gut.\n\nNoch ein Absatz danach.", "Der Kreis ist rot.\n\nUnd schwarz getrunken? Gut.\n\nNoch ein Absatz danach.")]
    [InlineData("Und schwarz getrunken? Ich frage mich, ob das hilft.", "Und schwarz getrunken? Ich frage mich, ob das hilft.")]
    [InlineData("Kaffee ist gesund.\n\nUnd schwarz getrunken schmeckt er am besten.", "Kaffee ist gesund.\n\nUnd schwarz getrunken schmeckt er am besten.")]
    public void RepeatedClosing_IsDropped(string text, string expected)
    {
        Assert.Equal(expected, RunLearned(text));
        Assert.Equal(expected, RunLearned([.. text.Select(c => c.ToString())]));
    }

    [Theory]
    // Selbsttest 55: hinter jedem Angebot ein "Beispiel"-Block ohne echten Code – Angebot und Block fallen weg.
    [InlineData("Wien hat 2 Mio.\n\nWillst du, dass ich noch eine Stadt hinzufüge?\n```python\n# Beispiel: Salzburg\n```", "Wien hat 2 Mio.\n\n")]
    [InlineData("Hier liegt alles.\n\nWillst du eine bestimmte Datei ansehen?\n```python\n# Beispiel: PLAN.md lesen\ndatei: PLAN.md\n```\n", "Hier liegt alles.\n\n")]
    [InlineData("Morgen 22 °C.\n\nWillst du noch Details für einen anderen Ort?\n```python\n# Beispiel: Wien", "Morgen 22 °C.\n\n")]
    public void OfferWithAnExampleBlock_IsDropped(string text, string expected)
    {
        Assert.Equal(expected, Run(text));
        Assert.Equal(expected, Run([.. text.Select(c => c.ToString())]));
    }

    [Theory]
    [InlineData("So geht es.\n\nWillst du die Klasse sehen?\n```csharp\npublic class Person { }\n```")]
    [InlineData("Text.\n\nMöchtest du mehr?\n```python\n# x\n```\n\nNoch ein Absatz.")]
    [InlineData("Möchtest du das so?\n```python\n# Vorschlag\n```")]
    public void OfferWithRealCode_OrMoreAfterIt_Stays(string text)
    {
        Assert.Equal(text, Run(text));
        Assert.Equal(text, Run([.. text.Select(c => c.ToString())]));
    }

    [Fact]
    public void ClosingParagraph_OnlyShortLastParagraphs()
    {
        Assert.Equal(EarlierClosing, ClosingFilter.ClosingParagraph("Die Kaution beträgt 2.380 €.\n\n" + EarlierClosing + "\n---"));
        Assert.Null(ClosingFilter.ClosingParagraph("Nur ein Absatz."));
        Assert.Null(ClosingFilter.ClosingParagraph("Text.\n\n- eins\n- zwei"));
        Assert.Null(ClosingFilter.ClosingParagraph("Text.\n\n```python\nprint(1)\n```"));
        Assert.Equal("und schwarz getrunken", ClosingFilter.FirstSentence("{verlauf:grau-blau}Und **schwarz** getrunken?{/verlauf} Ich …"));
    }

    [Fact]
    public void OnlyAQuestion_Stays() => Assert.Equal("Soll ich das für C# oder Python schreiben?", Run("Soll ich das für C# oder Python schreiben?"));

    [Theory]
    [InlineData("Text.\n\n{cyan}Falls du mehr wissen willst{/cyan}, frag einfach.", "Text.\n\n")]
    [InlineData("Text.\n\n{verlauf:rot-gold}Möchtest du mehr?{/verlauf}", "Text.\n\n")]
    [InlineData("Text.\n\n{cyan}Wichtig{/cyan}: vorher sichern.", "Text.\n\n{cyan}Wichtig{/cyan}: vorher sichern.")]
    public void ColorTagsBeforeTheOffer_AreSkipped(string text, string expected)
    {
        Assert.Equal(expected, Run(text));
        Assert.Equal(expected, Run([.. text.Select(c => c.ToString())]));
    }

    [Theory]
    [InlineData("Text.\n\nWenn du Windows nutzt, geht es anders.")]
    [InlineData("Text.\n\n```python\nSoll ich = 1\n```\n")]
    [InlineData("Text.\n\nSollich ist kein Wort.")]
    public void OtherEndings_Stay(string text) => Assert.Equal(text, Run(text));

    [Fact]
    public void OfferWithOptions_IsDroppedCompletely() =>
        Assert.Equal("Text.\n\n", Run("Text.\n\nMöchtest du mehr wissen?\n- Theorie\n- Praxis"));

    [Fact]
    public void ListAfterAnOffer_InTheMiddle_StaysInOrder()
    {
        const string text = "Text.\n\nMöchtest du mehr?\n- A\n- B\n\nWeiter geht's.";
        Assert.Equal(text, Run(text));
    }

    [Theory]
    [InlineData("Gut.\n\nWas willst du machen?\n- Plaudern\n- Arbeiten", "Gut.\n\nWas willst du machen?")]
    [InlineData("Schritte:\n- Erst das\n- Dann das", "Schritte:\n- Erst das\n- Dann das")]           // keine Frage davor
    [InlineData("Welche?\n- Nur eine", "Welche?\n- Nur eine")]                                          // eine Zeile ist kein Menü
    public void TrailingOptions_AreLeftOutOfTheHistory(string text, string expected) =>
        Assert.Equal(expected, ClosingFilter.WithoutTrailingOptions(text));

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
    public void CallInACodeBlock_AfterSomeText_IsHeldToo()
    {
        // Selbsttest 50: "Ich werde mir das genauer ansehen …" und dann der Aufruf in einem bash-Block.
        var gate = new ElementGate { IsCallLine = l => l.StartsWith("websuche:") };
        Assert.Equal("Ich sehe nach.\n\n", gate.Push("Ich sehe nach.\n\n```bash\nwebsuche: Wien\n```\n"));
        Assert.Equal((Max.Tools.ToolCall.MaybeBlockName, "websuche: Wien\n"), gate.Closed);
    }

    [Fact]
    public void CallInACodeBlock_EndingWithoutNewline_IsHeldToo()
    {
        var gate = new ElementGate { IsCallLine = l => l.StartsWith("websuche:") };
        Assert.Equal("Ich sehe nach.\n", gate.Push("Ich sehe nach.\n```bash\nwebsuche: Wien"));
        Assert.Equal("", gate.Flush());
        Assert.Equal((Max.Tools.ToolCall.MaybeBlockName, "websuche: Wien"), gate.Closed);
    }

    [Fact]
    public void RealCode_AfterSomeText_FlowsAsCode()
    {
        var gate = new ElementGate { IsCallLine = l => l.StartsWith("websuche:") };
        var text = "Zum Beispiel:\n\n```bash\nls -la\n```\nDanach:\n```bash\n\nwebsuche: ist hier nur Text\n```\nFertig.";
        var output = gate.Push("Zum Beispiel:\n\n```bash\nls -la\n```\nDanach:\n");
        Assert.Null(gate.Closed);
        Assert.Equal("Zum Beispiel:\n\n```bash\nls -la\n```\nDanach:\n", output);
        // Der zweite Block ist ein Aufruf – nach dem schließenden ``` des ersten wird wieder geprüft.
        Assert.Equal("", gate.Push("```bash\n\nwebsuche: ist hier nur Text\n```\nFertig."));
        Assert.NotNull(gate.Closed);
        Assert.Equal("```bash\n\nwebsuche: ist hier nur Text\n```\nFertig.", gate.Accept());
        Assert.Equal(text, output + "```bash\n\nwebsuche: ist hier nur Text\n```\nFertig.");
    }

    [Fact]
    public void BlockAfterAQuestion_IsAnExample_NotACall()
    {
        var gate = new ElementGate { IsCallLine = l => l.StartsWith("datei:") };
        var text = "Hier liegt alles.\n\nWillst du eine bestimmte Datei ansehen?\n```python\ndatei: PLAN.md\n```\n";
        Assert.Equal(text, string.Concat(text.Select(c => gate.Push(c.ToString()))) + gate.Flush());
        Assert.Null(gate.Closed);
    }

    [Fact]
    public void ClosingFenceOfShownCode_IsNotMistakenForANewBlock()
    {
        // Wäre das schließende ``` der Anfang eines neuen Blocks, würde "weiter" als Aufruf zurückgehalten.
        var gate = new ElementGate { IsCallLine = l => l == "weiter" };
        Assert.Equal("Text\n```python\nx = 1\n```\nweiter\n", gate.Push("Text\n```python\nx = 1\n```\nweiter\n"));
        Assert.Null(gate.Closed);
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
        Assert.Contains("answer ::= item* ( \"```\" code | \"```\" widget ( nl item* ( \"```\" code )? )? )? ( \"```\" w-frage [ \\n]* )?", gbnf);   // ein Element, Menü nur am Ende
        Assert.DoesNotContain("w-frage |", gbnf.Split("widget ::= ")[1].Split('\n')[0]);   // "Wort}" statt "{/verlauf}" geht nicht
        Assert.Contains("unit ::= ( [%\\u20ac$\\u00b0] | \" \" [^0-9:", gbnf);
    }

    [Fact]
    public void Colorful_AnswerStartsWithAGradient()
    {
        var gbnf = AnswerGrammar.Build(colorful: true);
        Assert.Contains("answer ::= [ \\n]* \"{verlauf\" ( \":\" grad )? \"}\" item* ( \"```\" code | \"```\" widget", gbnf);
        Assert.Contains("answer ::= item* ", AnswerGrammar.Build());
    }

    [Fact]
    public void WithoutWidgets_OnlyTextCodeAndTheMenuRemain()
    {
        var gbnf = AnswerGrammar.Build(widgets: false);
        Assert.Contains("answer ::= item* ( \"```\" code )? ( \"```\" w-frage [ \\n]* )?\n", gbnf);
    }

    [Theory]
    [InlineData(new[] { "Schreib ab jetzt bitte alles schön bunt." }, true)]
    [InlineData(new[] { "Mit Farbverläufen bitte!" }, true)]
    [InlineData(new[] { "Wie geht's?" }, false)]
    [InlineData(new[] { "Schau dir das Bild an: Welche Farbe hat der Kreis?" }, false)]
    [InlineData(new[] { "Lösch bitte den Verlauf." }, false)]
    [InlineData(new[] { "Schreib bunt.", "Danke, jetzt bitte keine Farben mehr." }, false)]
    [InlineData(new[] { "Schreib bunt.", "Wieder normal, ohne Farben." }, false)]
    [InlineData(new[] { "Keine Farben bitte.", "Doch wieder bunt!" }, true)]
    public void ColorWish_TheLastWordCounts(string[] said, bool colorful) =>
        Assert.Equal(colorful, LlmBackend.WantsColors(said.Select(t => new ChatMessage(ChatRole.User, t, DateTime.Now))));

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

public class LongConversationTests
{
    private static Conversation Long(int turns)
    {
        var conversation = new Conversation();
        for (var i = 0; i < turns; i++)
        {
            conversation.AddUser($"Frage {i}: Erzähl mir etwas über das Thema Nummer {i}, gern ausführlich und mit Beispielen dazu.");
            conversation.AddAssistant($"Antwort {i}: " + new string('x', 250));
        }
        conversation.AddUser("Und was war am Anfang?");
        return conversation;
    }

    private static async Task<List<ReplyChunk>> Reply(LlmBackend backend, Conversation conversation)
    {
        var chunks = new List<ReplyChunk>();
        await foreach (var chunk in backend.StreamReplyAsync(conversation, CancellationToken.None))
            chunks.Add(chunk);
        await backend.CompleteAsync();
        return chunks;
    }

    [Fact]
    public async Task TooLong_TheBeginningIsSummarized_NotJustDropped()
    {
        // Budget: 4600 - 1024 Antwort - 512 Denken = 3064 Zeichen (ein Zeichen = ein Token beim Test-Modell) – das Gespräch hat
        // gut 3400, der Auftrag zum Zusammenfassen passt aber noch in den ganzen Kontext.
        var model = new FakeModel("- Es ging um die Themen 0 bis 3.\n", "- Der Nutzer mag Beispiele.\n", null, "Am Anfang ging es um Thema 0.", null) { ContextSize = 4600 };
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => false));
        var conversation = Long(8);

        var chunks = await Reply(backend, conversation);

        Assert.Contains(chunks, c => c.IsStatus && c.Text.Contains("Anfang"));
        Assert.Equal("- Es ging um die Themen 0 bis 3.\n- Der Nutzer mag Beispiele.", conversation.Summary);
        Assert.True(conversation.SummarizedCount > 0);
        var prompt = model.Decode(model.LastPromptBeforeSampler);
        Assert.Contains("## Früher in diesem Gespräch", prompt);
        Assert.Contains("- Der Nutzer mag Beispiele.", prompt);
        Assert.DoesNotContain("Frage 0:", prompt);
        Assert.Contains("Und was war am Anfang?", prompt);
        Assert.Equal("Am Anfang ging es um Thema 0.", string.Concat(chunks.Where(c => !c.IsStatus && !c.IsTool).Select(c => c.Text)));
    }

    [Fact]
    public async Task Short_NoSummary()
    {
        var model = new FakeModel("Kurz.", null);
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => false));
        var conversation = Long(2);

        var chunks = await Reply(backend, conversation);

        Assert.DoesNotContain(chunks, c => c.IsStatus);
        Assert.Null(conversation.Summary);
    }

    [Fact]
    public void Fallback_TheQuestionsOfTheDroppedPart()
    {
        var conversation = Long(3);
        conversation.Summarize("- Alte Notiz.", 2);
        Assert.Equal("- Alte Notiz.\n- Der Nutzer fragte: Frage 1: Erzähl mir etwas über das Thema Nummer 1, gern ausführlich und mit Beispielen dazu.",
            LlmBackend.PlainSummary(conversation, 4));
    }
}

public class AnswerGrammarMatchTests
{
    private static readonly GbnfMatcher Grammar = new(AnswerGrammar.Build());

    /// <summary>Die Antworten von Max aus den Trainingsbeispielen (ohne Werkzeug-Aufrufe), wie build_dataset.py sie liest.</summary>
    public static IEnumerable<(string Name, string Answer)> TrainingAnswers()
    {
        foreach (var file in Directory.GetFiles(AudioTests.RepoFile("training/beispiele"), "*.txt").Order())
        {
            string? name = null, role = null;
            var lines = new List<string>();
            IEnumerable<(string, string)> Flush()
            {
                var text = string.Join('\n', lines).Trim('\n');
                if (role == "max" && !text.StartsWith("```werkzeug", StringComparison.Ordinal))
                    yield return ($"{Path.GetFileNameWithoutExtension(file)}/{name}", text);
            }
            foreach (var line in File.ReadAllLines(file))
            {
                if (line.StartsWith("=== ", StringComparison.Ordinal) || line.Trim() is "@nutzer" or "@max" or "@denken" or "@ergebnis" or "@gedaechtnis")
                {
                    foreach (var answer in Flush())
                        yield return answer;
                    if (line.StartsWith("=== ", StringComparison.Ordinal))
                    {
                        name = line[4..].Trim();
                        role = null;
                    }
                    else
                    {
                        role = line.Trim()[1..];
                    }
                    lines.Clear();
                }
                else if (role is not null)
                {
                    lines.Add(line);
                }
            }
            foreach (var answer in Flush())
                yield return answer;
        }
    }

    [Theory]
    [InlineData("Hallo {rot}Welt{/rot}, mit `Code` im Satz.")]
    [InlineData("Hier:\n```bash\necho hi\n```\nDanach.")]
    [InlineData("```python\nprint(1)\n```")]                                    // Code ganz am Ende, ohne Zeilenumbruch danach
    [InlineData("```python\nprint(1)\n```\n")]
    [InlineData("Text\n```balken\nA: 1\nB: 2\n```\nDanach mit\n```bash\nls\n```")]
    [InlineData("```balken\nA: 1\nB: 2\n```")]
    [InlineData("Fertig.\n\n```frage\nFrage: Was nun?\n- Plaudern\n- Arbeiten\n```")]
    [InlineData("```balken\nA: 1\n```\n```frage\nFrage: Was nun?\n- Mehr\n- Weniger\n```")]
    public void Grammar_Accepts(string answer) => Assert.True(Grammar.Accepts(answer));

    [Theory]
    [InlineData("Hallo {foo}.")]                                                 // unbekanntes Tag
    [InlineData("```klingon\nx\n```")]                                           // unbekannte Sprache
    [InlineData("```balken\nA: 1\n```\n```baum\nx\n```")]                        // zwei Elemente
    [InlineData("**Die Architektur:**\n\n```bash\n```baum\n```bash\n```fortschritt\nModel: 70-90%\n")]   // Selbsttest 56
    [InlineData("```bash\n```")]                                                 // leerer Block
    [InlineData("```text\n```")]
    [InlineData("```bash\necho hi\n```baum\nx")]                                 // nach dem Schluss-``` weiter in derselben Zeile
    [InlineData("```balken\nA: 1\n```Danach.")]
    public void Grammar_Rejects(string answer) => Assert.False(Grammar.Accepts(answer));

    [Fact]
    public void WithoutWidgets_CodeBlocksFollowTheSameRules()
    {
        var grammar = new GbnfMatcher(AnswerGrammar.Build(widgets: false));
        Assert.True(grammar.Accepts("Text\n```bash\nls\n```\nmehr\n```bash\nls\n```"));
        Assert.False(grammar.Accepts("```bash\n```baum\n"));
        Assert.False(grammar.Accepts("```balken\nA: 1\n```"));
    }

    [Fact]
    public void EveryTrainingAnswer_FitsTheGrammar()
    {
        var answers = TrainingAnswers().ToList();
        Assert.True(answers.Count > 500, $"{answers.Count} Antworten");
        var rejected = answers.Where(a => !Grammar.Accepts(a.Answer)).Select(a => a.Name).ToList();
        Assert.True(rejected.Count == 0, "Passt nicht zur Grammatik: " + string.Join(", ", rejected));
    }
}
