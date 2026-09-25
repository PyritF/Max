namespace Max.Commands;

/// <summary>Kennt alle Befehle und erkennt sie in der Eingabe.</summary>
internal sealed class CommandRegistry
{
    private readonly List<ICommand> _commands;
    private readonly Dictionary<string, ICommand> _byName = new(StringComparer.OrdinalIgnoreCase);

    public CommandRegistry(IEnumerable<ICommand> commands)
    {
        _commands = commands.ToList();
        foreach (var command in _commands)
        {
            _byName[command.Name] = command;
            foreach (var alias in command.Aliases)
                _byName[alias] = command;
        }
    }

    /// <param name="extra">Zusätzliche Befehle, z. B. /debug, das Zugriff auf das geladene Modell braucht.</param>
    public static CommandRegistry CreateDefault(params ICommand[] extra) => new(
    [
        new HelpCommand(),
        new ClearCommand(),
        new ExitCommand(),
        .. extra,
    ]);

    /// <summary>Alle Befehle für /help – ohne die versteckten.</summary>
    public IEnumerable<ICommand> Visible => _commands.Where(c => !c.Hidden);

    /// <summary>Beginnt die Eingabe mit "/"?</summary>
    public static bool IsCommand(string input) => input.TrimStart().StartsWith('/');

    /// <summary>"/Help  foo bar" → ("help", "foo bar").</summary>
    public static (string Name, string Args) Parse(string input)
    {
        var text = input.Trim().TrimStart('/');
        var space = text.IndexOfAny([' ', '\t']);
        return space < 0
            ? (text.ToLowerInvariant(), "")
            : (text[..space].ToLowerInvariant(), text[(space + 1)..].Trim());
    }

    public ICommand? Find(string name) => _byName.GetValueOrDefault(name);
}
