namespace Bazaar.Contracts.Modules;

/// <summary>
/// Registration surface a module writes to. The head creates one per module, after the manifest validates, and
/// the module adds its pane kinds and commands through it. Nothing is registered until the head calls
/// <see cref="IClientModule.Register"/>.
/// </summary>
public sealed class ClientModuleBuilder
{
    private readonly HashSet<string> _paneKinds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _commands = new(StringComparer.Ordinal);
    private readonly List<ClientCommandContribution> _contributions = [];

    public ClientModuleBuilder(ModuleManifest manifest)
    {
        Manifest = manifest;
    }

    public ModuleManifest Manifest { get; }

    /// <summary>Records a pane kind. Throws when the manifest does not declare it, so a module cannot register outside its manifest.</summary>
    public void AddPaneKind(string kind)
    {
        if (!Manifest.PaneKinds.Contains(kind, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Pane kind '{kind}' is not in the manifest of {Manifest.Id}.");
        }

        _paneKinds.Add(kind);
    }

    /// <summary>Records a command. Throws when the manifest does not declare it.</summary>
    public void AddCommand(string command)
    {
        if (!Manifest.Commands.Contains(command, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Command '{command}' is not in the manifest of {Manifest.Id}.");
        }

        _commands.Add(command);
    }

    /// <summary>
    /// Records a command with its default mnemonics. Throws when the manifest does not declare the id, when a
    /// mnemonic is malformed, or when the command was already added.
    /// </summary>
    public void AddCommand(ClientCommandContribution command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_commands.Contains(command.Id))
        {
            throw new InvalidOperationException($"Command '{command.Id}' was already added by {Manifest.Id}.");
        }

        if (string.IsNullOrWhiteSpace(command.Title))
        {
            throw new InvalidOperationException($"Command '{command.Id}' of {Manifest.Id} has no title.");
        }

        foreach (var mnemonic in command.Aliases.Prepend(command.Mnemonic))
        {
            if (!ClientCommandContribution.IsValidMnemonic(mnemonic))
            {
                throw new InvalidOperationException($"Mnemonic '{mnemonic}' of command '{command.Id}' is not lowercase letters and digits (at most {ClientCommandContribution.MaxMnemonicLength}).");
            }
        }

        AddCommand(command.Id);
        _contributions.Add(command);
    }

    /// <summary>The commands added with their mnemonics, in the order the module added them.</summary>
    public IReadOnlyList<ClientCommandContribution> Contributions => _contributions;

    public IReadOnlyCollection<string> RegisteredPaneKinds => _paneKinds;

    public IReadOnlyCollection<string> RegisteredCommands => _commands;
}

/// <summary>Contract every client module implements (doc 41 section 8). Heads call <see cref="Register"/> once.</summary>
public interface IClientModule
{
    ModuleManifest Manifest { get; }

    void Register(ClientModuleBuilder builder);
}
