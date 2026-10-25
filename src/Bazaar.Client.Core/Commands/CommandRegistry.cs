namespace Bazaar.Client.Core.Commands;

/// <summary>A server command that was not loaded, with the reason.</summary>
public sealed record SkippedCommand(Command Command, string Reason);

/// <summary>The registry plus any server commands skipped while loading it.</summary>
public sealed record CommandLoadResult(CommandRegistry Registry, IReadOnlyList<SkippedCommand> SkippedServerCommands);

/// <summary>
/// Immutable set of commands. Duplicate ids and duplicate local mnemonics fail the load.
/// Server commands whose mnemonic conflicts with an earlier command are skipped and reported.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, Command> _byId;
    private readonly Dictionary<string, Command> _byMnemonic;

    private CommandRegistry(IReadOnlyList<Command> commands, Dictionary<string, Command> byMnemonic)
    {
        Commands = commands;
        _byMnemonic = byMnemonic;
        _byId = commands.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    /// <summary>Local commands first, then accepted server commands, in input order.</summary>
    public IReadOnlyList<Command> Commands { get; }

    /// <summary>
    /// Loads local and server commands.
    /// Throws <see cref="InvalidOperationException"/> on a duplicate id or a duplicate local mnemonic.
    /// </summary>
    public static CommandLoadResult Load(IEnumerable<Command> localCommands, IEnumerable<Command> serverCommands)
    {
        ArgumentNullException.ThrowIfNull(localCommands);
        ArgumentNullException.ThrowIfNull(serverCommands);

        var commands = new List<Command>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var mnemonics = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);

        foreach (var command in localCommands)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (command.Source != CommandSource.Local)
            {
                throw new ArgumentException($"Command '{command.Id}' is not a local command.", nameof(localCommands));
            }

            if (!ids.Add(command.Id))
            {
                throw new InvalidOperationException($"Duplicate command id '{command.Id}'.");
            }

            if (mnemonics.TryGetValue(command.Mnemonic, out var owner))
            {
                throw new InvalidOperationException(
                    $"Local commands '{owner.Id}' and '{command.Id}' share the mnemonic '{command.Mnemonic}'.");
            }

            mnemonics.Add(command.Mnemonic, command);
            commands.Add(command);
        }

        var skipped = new List<SkippedCommand>();
        foreach (var command in serverCommands)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (command.Source != CommandSource.Server)
            {
                throw new ArgumentException($"Command '{command.Id}' is not a server command.", nameof(serverCommands));
            }

            if (!ids.Add(command.Id))
            {
                throw new InvalidOperationException($"Duplicate command id '{command.Id}'.");
            }

            if (mnemonics.TryGetValue(command.Mnemonic, out var owner))
            {
                skipped.Add(new SkippedCommand(
                    command,
                    $"Mnemonic '{command.Mnemonic}' is already used by '{owner.Id}'."));
                continue;
            }

            mnemonics.Add(command.Mnemonic, command);
            commands.Add(command);
        }

        return new CommandLoadResult(new CommandRegistry(commands.AsReadOnly(), mnemonics), skipped.AsReadOnly());
    }

    /// <summary>Exact id lookup, or null.</summary>
    public Command? FindById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>Exact, case-insensitive mnemonic lookup, used when a chord runs on Enter. Prefixes do not match.</summary>
    public Command? FindByMnemonic(string mnemonic)
    {
        ArgumentNullException.ThrowIfNull(mnemonic);
        return _byMnemonic.TryGetValue(mnemonic, out var command) ? command : null;
    }
}
