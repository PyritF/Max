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
    public void Assistant_StartsWithEmptyThinkBlock_AlsoInHistory()
    {
        Assert.Equal("<|im_start|>assistant\n<think>\n\n</think>\n\n", _template.AssistantStart);
        Assert.Equal(_template.AssistantStart + "Guten Abend.<|im_end|>\n", _template.Message(ChatRole.Assistant, "Guten Abend."));
    }
}

public class ThinkFilterTests
{
    private static string Run(params string[] chunks)
    {
        var filter = new ThinkFilter();
        var output = new StringBuilder();
        foreach (var chunk in chunks)
            output.Append(filter.Push(chunk));
        return output.Append(filter.Flush()).ToString();
    }

    [Fact]
    public void PlainText_PassesThrough() => Assert.Equal("Hallo Welt", Run("Hallo", " Welt"));

    [Fact]
    public void ThinkBlock_IsRemoved() => Assert.Equal("Antwort", Run("<think>grübel grübel</think>\n\nAntwort"));

    [Fact]
    public void Tags_SplitAcrossChunks() => Assert.Equal("vorher Antwort", Run("vorher <th", "ink>geh", "eim</thi", "nk>Antwort"));

    [Fact]
    public void UnclosedThinkBlock_ShowsNothing() => Assert.Equal("", Run("<think>denke", " und denke"));

    [Fact]
    public void LookalikeTag_IsKept() => Assert.Equal("a <thin client", Run("a <thin", " client"));

    [Fact]
    public void AngleBracketAtEnd_IsFlushed() => Assert.Equal("x < y <", Run("x < y <"));

    [Fact]
    public void ReportsDroppedText()
    {
        var filter = new ThinkFilter();
        filter.Push("ohne Denken");
        Assert.False(filter.DroppedAnything);
        filter.Push("<think>doch</think>");
        Assert.True(filter.DroppedAnything);
    }
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
        var system = new SystemSnapshot(new DateTime(2026, 9, 25, 21, 14, 0), "alex", "Windows 11", 16,
            new HardwareInfo(32L << 30, null), @"C:\Users\alex");
        var prompt = SystemPrompt.Build(system);

        Assert.StartsWith("Du bist Max.", prompt);
        Assert.DoesNotContain("{{", prompt);
        Assert.Contains("Freitag, 25. September 2026", prompt);
        Assert.Contains("21:14", prompt);
        Assert.Contains("Windows 11", prompt);
        Assert.Contains("alex", prompt);
        Assert.DoesNotContain("\r", prompt);
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
    [Fact]
    public async Task Prompt_IsSystem_History_AssistantStart()
    {
        var model = new FakeModel(["Hallo", " zurück."]);
        var backend = new LlmBackend(model, "SYS");
        var conversation = new Conversation();
        conversation.AddUser("Hi");

        var reply = await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));

        Assert.Equal("Hallo zurück.", reply);
        var template = new ChatMlTemplate();
        var expected = template.Message(ChatRole.System, "SYS") + template.Message(ChatRole.User, "Hi") + template.AssistantStart;
        Assert.Equal(expected, model.Decode(model.Prompts[0]));
    }

    [Fact]
    public async Task NextPrompt_ContinuesExactlyWhereTheModelStopped()
    {
        // Das Modell erzeugt Tokens, die NICHT dem entsprechen, was Tokenize() aus dem Text machen würde –
        // trotzdem muss der nächste Prompt mit genau diesen Tokens weitergehen (sonst ist der Cache wertlos).
        var model = new FakeModel(["Ant", "wort"]);
        var backend = new LlmBackend(model, "SYS");
        var conversation = new Conversation();

        conversation.AddUser("Eins");
        conversation.AddAssistant(await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None)));
        conversation.AddUser("Zwei");
        await Collect(backend.StreamReplyAsync(conversation, CancellationToken.None));

        var first = model.Prompts[0];
        var second = model.Prompts[1];
        Assert.Equal(first, second.Take(first.Count));
        Assert.Equal(model.GeneratedTokens[0], second.Skip(first.Count).Take(model.GeneratedTokens[0].Count));
    }

    [Fact]
    public async Task ThinkText_IsHidden_AndLeadingBlankLinesTrimmed()
    {
        var model = new FakeModel(["<think>", "hm", "</think>", "\n\n", "Klar."]);
        var reply = await Collect(new LlmBackend(model, "SYS").StreamReplyAsync(Single("?"), CancellationToken.None));
        Assert.Equal("Klar.", reply);
    }

    [Fact]
    public async Task Cancellation_IsPassedThrough()
    {
        using var cts = new CancellationTokenSource();
        var model = new FakeModel(["a", "b", "c"], onPiece: i => { if (i == 1) cts.Cancel(); });
        var received = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in new LlmBackend(model, "SYS").StreamReplyAsync(Single("?"), cts.Token))
                received.Add(chunk);
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

    private static async Task<string> Collect(IAsyncEnumerable<string> chunks)
    {
        var text = new StringBuilder();
        await foreach (var chunk in chunks)
            text.Append(chunk);
        return text.ToString();
    }

    /// <summary>
    /// Attrappe: ein Token pro Zeichen beim Zerlegen. Beim Erzeugen dagegen ein Token pro Stück
    /// (IDs ab 100.000) – so fällt auf, wenn der Backend den Text neu zerlegt statt die echten Tokens zu nehmen.
    /// </summary>
    private sealed class FakeModel(string[] pieces, Action<int>? onPiece = null) : ILanguageModel
    {
        private readonly Dictionary<int, string> _generatedText = [];
        private int _next = 100_000;

        public List<IReadOnlyList<int>> Prompts { get; } = [];
        public List<List<int>> GeneratedTokens { get; } = [];
        public int ContextSize => 100_000;

        public IReadOnlyList<int> Tokenize(string text) => text.Select(c => (int)c).ToArray();

        public string Decode(IEnumerable<int> tokens) =>
            string.Concat(tokens.Select(t => _generatedText.TryGetValue(t, out var s) ? s : ((char)t).ToString()));

        public async IAsyncEnumerable<GeneratedPiece> GenerateAsync(IReadOnlyList<int> prompt, SamplingSettings sampling, [EnumeratorCancellation] CancellationToken ct)
        {
            Prompts.Add(prompt.ToArray());
            var generated = new List<int>();
            GeneratedTokens.Add(generated);
            for (var i = 0; i < pieces.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                var token = _next++;
                _generatedText[token] = pieces[i];
                generated.Add(token);
                yield return new GeneratedPiece(token, pieces[i]);
                onPiece?.Invoke(i);
            }
        }
    }
}
