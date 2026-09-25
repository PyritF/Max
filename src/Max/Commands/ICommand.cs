using Max.Chat;
using Max.Ui;
using Spectre.Console;

namespace Max.Commands;

/// <summary>Was nach einem Befehl passieren soll.</summary>
internal enum CommandResult
{
    Continue,
    Exit,
}

/// <summary>Worauf ein Befehl zugreifen darf.</summary>
/// <param name="OfferQuestion">Zeigt nach dem Befehl ein Auswahlmenü (null, wenn es keins gibt, z. B. ohne echtes Terminal).</param>
internal sealed record CommandContext(IAnsiConsole Console, Conversation Conversation, CommandRegistry Registry, Action<ChoiceQuestion>? OfferQuestion = null);

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
