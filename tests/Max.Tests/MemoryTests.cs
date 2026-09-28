using Max.Chat;
using Max.Commands;
using Max.Llm;
using Max.Memory;
using Max.Persona;
using Max.Setup;
using Max.Ui;
using Spectre.Console.Testing;

namespace Max.Tests;

public class MemoryDataTests
{
    private static readonly DateOnly Today = new(2026, 9, 28);

    [Fact]
    public void WithFacts_SkipsDuplicatesAndEmptyLines()
    {
        var memory = MemoryData.Empty.WithFacts(["Programmiert in C#.", "  ", "- programmiert in C#", "Trinkt Kaffee schwarz."], Today);

        Assert.Equal(["Programmiert in C#.", "Trinkt Kaffee schwarz."], memory.Facts.Select(f => f.Text));
        Assert.All(memory.Facts, f => Assert.Equal(Today, f.Added));
    }

    [Fact]
    public void WithFacts_KeepsOnlyTheNewest()
    {
        var memory = MemoryData.Empty.WithFacts(Enumerable.Range(1, MemoryData.MaxFacts + 5).Select(i => $"Fakt Nummer {i}."), Today);

        Assert.Equal(MemoryData.MaxFacts, memory.Facts.Count);
        Assert.Equal("Fakt Nummer 6.", memory.Facts[0].Text);
    }

    [Fact]
    public void Without_RemovesByNumber()
    {
        var memory = MemoryData.Empty.WithFacts(["Eins.", "Zwei.", "Drei."], Today);

        Assert.Equal(["Eins.", "Drei."], memory.Without(2)!.Facts.Select(f => f.Text));
        Assert.Null(memory.Without(0));
        Assert.Null(memory.Without(4));
    }

    [Theory]
    [InlineData(7, "Morgen")]
    [InlineData(14, "Tag")]
    [InlineData(20, "Abend")]
    [InlineData(2, "Nacht")]
    public void Greeting_MatchesTheTimeOfDay(int hour, string expected)
    {
        var greetings = new Greetings("Morgen", "Tag", "Abend", "Nacht", new DateTime(2026, 9, 27, 23, 0, 0));
        Assert.Equal(expected, greetings.For(new DateTime(2026, 9, 28, hour, 0, 0)));
    }

    [Fact]
    public void Greeting_TooOld_IsNotUsed()
    {
        var greetings = new Greetings("Morgen", "Tag", "Abend", "Nacht", new DateTime(2026, 9, 1));
        Assert.Null(greetings.For(new DateTime(2026, 9, 28, 9, 0, 0)));
    }
}

public sealed class MemoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "max-memory-" + Guid.NewGuid().ToString("N"));
    private readonly MaxPaths _paths;

    public MemoryStoreTests() => _paths = new MaxPaths(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var memory = MemoryData.Empty.WithFacts(["Programmiert in C#."], new DateOnly(2026, 9, 28)) with
        {
            LastSession = new LastSession(new DateTime(2026, 9, 27, 23, 41, 0), "Markdown-Renderer"),
            NextGreeting = new Greetings("M", "T", "A", "N", new DateTime(2026, 9, 27, 23, 41, 0)),
        };
        MemoryStore.Save(_paths, memory);

        var loaded = MemoryStore.Load(_paths);
        Assert.Equal("Programmiert in C#.", loaded.Facts.Single().Text);
        Assert.Equal("Markdown-Renderer", loaded.LastSession!.Summary);
        Assert.Equal("A", loaded.NextGreeting!.Evening);
        Assert.Contains("\"text\": \"Programmiert in C#.\"", File.ReadAllText(_paths.Memory));
    }

    [Fact]
    public void MissingOrBrokenFile_StartsEmpty()
    {
        Assert.Empty(MemoryStore.Load(_paths).Facts);
        _paths.EnsureExists();
        File.WriteAllText(_paths.Memory, "{ kaputt");
        Assert.Empty(MemoryStore.Load(_paths).Facts);
    }

    [Fact]
    public void Greeting_IsTakenOnlyOnce()
    {
        var created = new DateTime(2026, 9, 27, 23, 0, 0);
        MemoryStore.Save(_paths, MemoryData.Empty with { NextGreeting = new Greetings("Zurück am Renderer?", "T", "A", "N", created) });

        var book = new MemoryBook(_paths);
        Assert.Equal("Zurück am Renderer?", book.TakeGreeting(created.AddHours(10)));
        Assert.Null(book.TakeGreeting(created.AddHours(10)));
        Assert.Null(new MemoryBook(_paths).TakeGreeting(created.AddHours(10)));   // auch nach einem Neustart
    }

    [Fact]
    public async Task Forget_ByNumberAndAll_SavesAndReportsTheChange()
    {
        MemoryData? changed = null;
        var book = new MemoryBook(_paths, m => changed = m);
        book.Set(MemoryData.Empty.WithFacts(["Eins.", "Zwei."], new DateOnly(2026, 9, 28)));
        var console = new TestConsole();
        var context = new CommandContext(console, new Conversation(), CommandRegistry.CreateDefault());

        await new MemoryCommand(book).ExecuteAsync(context, "");
        Assert.Contains("Zwei.", console.Output);

        await new ForgetCommand(book).ExecuteAsync(context, "1");
        Assert.Equal(["Zwei."], MemoryStore.Load(_paths).Facts.Select(f => f.Text));
        Assert.Equal(["Zwei."], changed!.Facts.Select(f => f.Text));
        Assert.Contains("Vergessen", console.Output);

        await new ForgetCommand(book).ExecuteAsync(context, "7");
        Assert.Contains("Welche Nummer", console.Output);

        await new ForgetCommand(book).ExecuteAsync(context, "alles");
        Assert.Empty(MemoryStore.Load(_paths).Facts);
    }
}

public class ReflectionTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 23, 41, 0);

    private static readonly string[] UserSaid =
    [
        "Übrigens: Ich programmiere beruflich in C#, und meinen Kaffee trinke ich schwarz.", "Was kann man in Wien machen?",
        "Ich hatte heute einen langen Tag.", "Erzähl mir was über den Herbst.", "Zeig mir eine typische Ordnerstruktur für C#.",
    ];

    private const string Sample = """
        FAKTEN:
        - Programmiert in C#. | "Ich programmiere beruflich in C#"
        - Trinkt Kaffee schwarz. | "meinen Kaffee trinke ich schwarz"
        - Wohnt in Wien. | "Was kann man in Wien machen?"
        - Hatte einen langen Tag. | "Ich hatte heute einen langen Tag."
        - Mag Tee. | "Ich trinke gern Tee"
        - Mag den Herbst. | "Erzähl mir was über den Herbst."
        - Ist berufstätig (impliziert). | "meinen Kaffee trinke ich schwarz"
        - Arbeitet als Entwickler. | "Ich programmiere beruflich in C#"
        - Nutzt C#-Ordner. | "Zeig mir eine typische Ordnerstruktur"
        ZUSAMMENFASSUNG: Tabellen im Renderer repariert
        MORGEN: Frisch ans Werk – die Tabellen halten hoffentlich noch.
        TAG: Zurück am Renderer?
        ABEND: "Wieder am Code? Ich hab die Tabellen im Auge behalten."
        NACHT: Spät dran. Die Tabellen schlafen schon.

        """;

    [Fact]
    public void Parse_ReadsFactsSummaryAndGreetings()
    {
        var reflection = Reflection.Parse(Sample, Now, UserSaid)!;

        // Wien: kein Satz über sich selbst; langer Tag: vorübergehend; Tee: so nie gesagt; Herbst, Ordner: Bitten an Max;
        // "impliziert" und ein zweiter Fakt aus demselben Satz: Ausdeutung.
        Assert.Equal(["Programmiert in C#.", "Trinkt Kaffee schwarz."], reflection.Facts);
        Assert.Equal("Tabellen im Renderer repariert", reflection.Summary);
        Assert.Equal("Wieder am Code? Ich hab die Tabellen im Auge behalten.", reflection.Greetings.Evening);   // ohne Anführungszeichen
        Assert.Equal(Now, reflection.Greetings.Created);
    }

    [Fact]
    public void Parse_NothingNew_AndBrokenAnswers()
    {
        Assert.Empty(Reflection.Parse("FAKTEN:\n- keine | \"ich\"\nZUSAMMENFASSUNG: Plauderei\nMORGEN: a\n", Now, UserSaid)!.Facts);
        Assert.Null(Reflection.Parse("Das Gespräch war nett.", Now, UserSaid));
        Assert.Null(Reflection.Parse("FAKTEN:\n- Mag Tee.\n", Now, UserSaid));      // ohne Zusammenfassung abgebrochen
        Assert.Null(Reflection.Parse($"FAKTEN:\nZUSAMMENFASSUNG: x\nTAG: {new string('a', 101)}\n", Now, UserSaid)!.Greetings.Day);
    }

    [Fact]
    public void IsComplete_AfterTheLastLine()
    {
        Assert.False(Reflection.IsComplete("FAKTEN:\nZUSAMMENFASSUNG: x\nNACHT: Spät"));
        Assert.True(Reflection.IsComplete("FAKTEN:\nZUSAMMENFASSUNG: x\nNACHT: Spät.\n"));
    }

    [Fact]
    public void ApplyTo_AddsFactsAndReplacesGreetingAndLastSession()
    {
        var old = MemoryData.Empty.WithFacts(["Programmiert in C#."], new DateOnly(2026, 9, 1));
        var updated = Reflection.Parse(Sample, Now, UserSaid)!.ApplyTo(old, Now);

        Assert.Equal(2, updated.Facts.Count);
        Assert.Equal(new DateOnly(2026, 9, 1), updated.Facts[0].Added);     // bekannter Fakt bleibt mit altem Datum
        Assert.Equal("Tabellen im Renderer repariert", updated.LastSession!.Summary);
        Assert.Equal("Zurück am Renderer?", updated.NextGreeting!.Day);
    }

    [Fact]
    public async Task RunAsync_AsksWithoutThinking_InFixedForm_AndLeavesTheHistoryAlone()
    {
        // Das Modell schreibt die Antwort Zeile für Zeile; nach der NACHT-Zeile ist Schluss, auch ohne Ende-Token.
        var lines = Sample.Split('\n').Where(l => l.Length > 0).Select(l => l.Trim() + "\n").ToList();
        var model = new FakeModel([.. lines, "Überflüssig"]);
        var backend = new LlmBackend(model, "Du bist Max.", new BackendOptions(ThinkingEnabled: () => true));
        var conversation = new Conversation();
        conversation.AddUser(UserSaid[0]);
        conversation.AddAssistant("Gute Wahl.");

        var reflection = await Reflection.RunAsync(backend, conversation.Messages, Now, CancellationToken.None);

        Assert.NotNull(reflection);
        Assert.Equal("Spät dran. Die Tabellen schlafen schon.", reflection.Greetings.Night);
        Assert.True(model.SamplerGrammars[^1]);
        var prompt = model.Decode(model.LastPromptBeforeSampler);
        Assert.Contains("Interne Aufgabe", prompt);
        Assert.EndsWith("<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt);   // ohne Nachdenken
        Assert.Equal(2, conversation.Messages.Count);
    }
}

public class MemoryPromptTests
{
    private static readonly SystemSnapshot System = new(new DateTime(2026, 9, 28, 9, 0, 0), UserIdentity.Create("alex", "Alex Beispiel"), "Windows 11", 16,
        new HardwareInfo(32L << 30, null), @"C:\Users\alex");

    [Fact]
    public void WithoutMemory_NoSection()
    {
        var prompt = SystemPrompt.Build(System);
        Assert.DoesNotContain("Was du über den Nutzer weißt", prompt);
        Assert.DoesNotContain("{{", prompt);
    }

    [Fact]
    public void Facts_ComeAtTheEnd_AndTheStablePartStaysTheSame()
    {
        var memory = MemoryData.Empty.WithFacts(["Programmiert in C#."], new DateOnly(2026, 9, 27)) with
        {
            LastSession = new LastSession(new DateTime(2026, 9, 27, 23, 41, 0), "Tabellen im Renderer"),
        };
        var without = SystemPrompt.BuildParts(System);
        var with = SystemPrompt.BuildParts(System, memory);

        Assert.EndsWith("- Letztes Gespräch (Sonntag, 27. September): Tabellen im Renderer", with.Text);
        Assert.Contains("\n## Was du über den Nutzer weißt\n", with.Text);
        Assert.Contains("- Programmiert in C#.\n", with.Text);
        Assert.Equal(without.StableLength, with.StableLength);
        Assert.StartsWith(without.Text, with.Text);
    }

    [Fact]
    public async Task UpdateSystemPrompt_TakesEffectWithTheNextAnswer()
    {
        var model = new FakeModel("Ja.", null, "Nein.");
        var backend = new LlmBackend(model, "Du bist Max.\n- Mag Tee.", new BackendOptions(ThinkingEnabled: () => false));
        var conversation = new Conversation();
        conversation.AddUser("Hallo");
        await foreach (var _ in backend.StreamReplyAsync(conversation, CancellationToken.None)) { }
        conversation.AddAssistant("Ja.");

        backend.UpdateSystemPrompt("Du bist Max.");
        conversation.AddUser("Und jetzt?");
        await foreach (var _ in backend.StreamReplyAsync(conversation, CancellationToken.None)) { }

        Assert.DoesNotContain("Mag Tee", model.Decode(model.LastPromptBeforeSampler));
    }

    [Theory]
    [InlineData(28, 14, "Heute, 14:05")]
    [InlineData(27, 23, "Gestern, 23:05")]
    [InlineData(24, 9, "Donnerstag")]
    [InlineData(2, 9, "2. September")]
    public void HomeScreen_LastSessionDate(int day, int hour, string expected) =>
        Assert.Equal(expected, HomeScreen.When(new DateTime(2026, 9, day, hour, 5, 0), new DateTime(2026, 9, 28, 20, 0, 0)));
}
