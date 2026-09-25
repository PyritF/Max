using Max.Chat;
using Spectre.Console;

namespace Max.Commands;

/// <summary>Was nach einem Befehl passieren soll.</summary>
internal enum CommandResult
{
    Continue,
    Exit,
}

/// <summary>Worauf ein Befehl zugreifen darf.</summary>
internal sealed record CommandContext(IAnsiConsole Console, Conversation Conversation, CommandRegistry Registry);

/// <summary>Ein Befehl, der mit "/" beginnt, z. B. /help.</summary>
internal interface ICommand
{
    /// <summary>Name ohne "/", klein geschrieben, z. B. "help".</summary>
    string Name { get; }

    /// <summary>Weitere Namen, z. B. "q" für "exit".</summary>
    IReadOnlyList<string> Aliases { get; }

    /// <summary>Kurzbeschreibung für /help.</summary>
    string Description { get; }

    /// <summary>Versteckte Befehle funktionieren, erscheinen aber nicht in /help.</summary>
    bool Hidden { get; }

    Task<CommandResult> ExecuteAsync(CommandContext context, string args);
}
