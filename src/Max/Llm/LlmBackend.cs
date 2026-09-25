using System.Runtime.CompilerServices;
using System.Text;
using Max.Chat;

namespace Max.Llm;

/// <summary>
/// Max' Antworten aus dem lokalen Sprachmodell: System-Prompt + Verlauf → Prompt → Modell → Text.
/// Ersetzt <see cref="PlaceholderBackend"/>; die Chat-Schleife merkt davon nichts.
/// </summary>
internal sealed class LlmBackend(ILanguageModel model, string systemPrompt, IChatTemplate? template = null, SamplingSettings? sampling = null)
    : IChatBackend
{
    /// <summary>So viele Tokens bleiben im Kontext für die Antwort frei.</summary>
    internal const int ReplyReserve = 1024;

    private readonly IChatTemplate _template = template ?? new ChatMlTemplate();
    private readonly SamplingSettings _sampling = sampling ?? new SamplingSettings();
    private readonly ContextWindow _window = new(Math.Max(256, model.ContextSize - ReplyReserve));

    // Tokens je Nachricht. Für eigene Antworten die tatsächlich erzeugten Tokens – nicht neu zerlegt,
    // damit der nächste Prompt exakt zum Cache des Modells passt.
    private readonly Dictionary<(ChatRole Role, string Content), IReadOnlyList<int>> _tokens = [];
    private IReadOnlyList<int>? _systemTokens;
    private IReadOnlyList<int>? _assistantStart;
    private IReadOnlyList<int>? _assistantEnd;

    /// <summary>Wie lange das Aufwärmen gedauert hat – für /debug.</summary>
    public TimeSpan? WarmUpTime { get; private set; }

    /// <summary>
    /// Rechnet den System-Prompt schon beim Start in den Cache. Dabei richtet sich auch die
    /// Grafikkarte ein (Vulkan übersetzt beim ersten Rechnen seine Shader) – die Wartezeit
    /// fällt so in den Ladebildschirm statt in die erste Antwort.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _systemTokens ??= model.Tokenize(_template.Message(ChatRole.System, systemPrompt));
        await model.PrefillAsync(_systemTokens, ct);
        WarmUpTime = clock.Elapsed;
    }

    public async IAsyncEnumerable<string> StreamReplyAsync(Conversation conversation, [EnumeratorCancellation] CancellationToken ct)
    {
        var prompt = BuildPrompt(conversation.Messages);
        var filter = new ThinkFilter();
        var shown = new StringBuilder();
        var generated = new List<int>();

        try
        {
            await foreach (var piece in model.GenerateAsync(prompt, _sampling, ct))
            {
                generated.Add(piece.Token);
                var visible = filter.Push(piece.Text);
                if (visible.Length == 0)
                    continue;

                // Führende Leerzeilen am Antwortanfang sehen im Terminal nach einem Fehler aus.
                if (shown.Length == 0)
                    visible = visible.TrimStart('\n', '\r', ' ');
                if (visible.Length == 0)
                    continue;

                shown.Append(visible);
                yield return visible;
            }

            var rest = filter.Flush();
            if (rest.Length > 0)
            {
                shown.Append(rest);
                yield return rest;
            }
        }
        finally
        {
            // Nur merken, wenn Text und Tokens sicher zusammenpassen.
            if (shown.Length > 0 && !filter.DroppedAnything)
                Remember(ChatRole.Assistant, shown.ToString(), generated);
        }
    }

    internal IReadOnlyList<int> BuildPrompt(IReadOnlyList<ChatMessage> messages)
    {
        _systemTokens ??= model.Tokenize(_template.Message(ChatRole.System, systemPrompt));
        _assistantStart ??= model.Tokenize(_template.AssistantStart);
        _assistantEnd ??= model.Tokenize(_template.AssistantEnd);

        var first = _window.FirstIncluded(messages, _systemTokens.Count + _assistantStart.Count, m => TokensOf(m).Count);

        var prompt = new List<int>(_systemTokens);
        for (var i = first; i < messages.Count; i++)
            prompt.AddRange(TokensOf(messages[i]));
        prompt.AddRange(_assistantStart);
        return prompt;
    }

    private IReadOnlyList<int> TokensOf(ChatMessage message)
    {
        var key = (message.Role, message.Content);
        if (!_tokens.TryGetValue(key, out var tokens))
        {
            tokens = model.Tokenize(_template.Message(message.Role, message.Content));
            _tokens[key] = tokens;
        }
        return tokens;
    }

    private void Remember(ChatRole role, string content, List<int> generated)
    {
        var tokens = new List<int>(_assistantStart!.Count + generated.Count + _assistantEnd!.Count);
        tokens.AddRange(_assistantStart);
        tokens.AddRange(generated);
        tokens.AddRange(_assistantEnd);
        _tokens[(role, content)] = tokens;
    }
}
