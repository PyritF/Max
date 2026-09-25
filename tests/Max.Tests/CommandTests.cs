using Max.Chat;
using Max.Commands;
using Spectre.Console.Testing;

namespace Max.Tests;

public class CommandTests
{
    private readonly TestConsole _console = new();
    private readonly Conversation _conversation = new();
    private readonly CommandRegistry _registry = CommandRegistry.CreateDefault();

    private CommandContext Context => new(_console, _conversation, _registry);

    [Fact]
    public async Task Help_listet_alle_Befehle()
    {
        var result = await new HelpCommand().ExecuteAsync(Context, "");

        Assert.Equal(CommandResult.Continue, result);
        Assert.Contains("/help", _console.Output);
        Assert.Contains("/clear", _console.Output);
        Assert.Contains("/exit", _console.Output);
    }

    [Fact]
    public async Task Clear_leert_den_Verlauf()
    {
        _conversation.AddUser("hallo");
        _conversation.AddAssistant("hi");

        var result = await new ClearCommand().ExecuteAsync(Context, "");

        Assert.Equal(CommandResult.Continue, result);
        Assert.Empty(_conversation.Messages);
    }

    [Fact]
    public async Task Exit_beendet_und_verabschiedet_sich()
    {
        var result = await new ExitCommand().ExecuteAsync(Context, "");

        Assert.Equal(CommandResult.Exit, result);
        Assert.Contains("◆", _console.Output);
    }
}
