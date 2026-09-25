using Max.Commands;

namespace Max.Tests;

public class CommandRegistryTests
{
    private readonly CommandRegistry _registry = CommandRegistry.CreateDefault();

    [Theory]
    [InlineData("/help", true)]
    [InlineData("   /help", true)]
    [InlineData("hallo", false)]
    [InlineData("was ist /help?", false)]
    public void IsCommand_erkennt_Schraegstrich_am_Anfang(string eingabe, bool erwartet)
    {
        Assert.Equal(erwartet, CommandRegistry.IsCommand(eingabe));
    }

    [Theory]
    [InlineData("/help", "help", "")]
    [InlineData("  /HELP  foo bar ", "help", "foo bar")]
    [InlineData("/exit\tjetzt", "exit", "jetzt")]
    public void Parse_trennt_Name_und_Argumente(string eingabe, string name, string args)
    {
        Assert.Equal((name, args), CommandRegistry.Parse(eingabe));
    }

    [Theory]
    [InlineData("help", "help")]
    [InlineData("?", "help")]
    [InlineData("q", "exit")]
    [InlineData("QUIT", "exit")]
    [InlineData("clear", "clear")]
    public void Find_kennt_Namen_und_Aliase(string name, string erwarteterBefehl)
    {
        Assert.Equal(erwarteterBefehl, _registry.Find(name)?.Name);
    }

    [Fact]
    public void Find_liefert_null_fuer_unbekannte_Befehle()
    {
        Assert.Null(_registry.Find("xyz"));
    }

    [Fact]
    public void Versteckte_Befehle_fehlen_in_der_Uebersicht_funktionieren_aber()
    {
        var registry = new CommandRegistry([new HelpCommand(), new GeheimerBefehl()]);

        Assert.DoesNotContain(registry.Visible, c => c.Name == "geheim");
        Assert.NotNull(registry.Find("geheim"));
    }

    private sealed class GeheimerBefehl : ICommand
    {
        public string Name => "geheim";
        public IReadOnlyList<string> Aliases => [];
        public string Description => "";
        public bool Hidden => true;
        public Task<CommandResult> ExecuteAsync(CommandContext context, string args) =>
            Task.FromResult(CommandResult.Continue);
    }
}
