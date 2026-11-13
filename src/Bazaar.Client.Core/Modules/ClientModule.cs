namespace Bazaar.Client.Core.Modules;

/// <summary>
/// Registration surface a module writes to. The head creates one per module, after the manifest validates, and
/// the module adds its pane kinds and commands through it. Nothing is registered until the head calls
/// <see cref="IClientModule.Register"/>.
/// </summary>
public sealed class ClientModuleBuilder
{
    private readonly HashSet<string> _paneKinds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _commands = new(StringComparer.Ordinal);

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

    public IReadOnlyCollection<string> RegisteredPaneKinds => _paneKinds;

    public IReadOnlyCollection<string> RegisteredCommands => _commands;
}

/// <summary>Contract every client module implements (doc 41 section 8). Heads call <see cref="Register"/> once.</summary>
public interface IClientModule
{
    ModuleManifest Manifest { get; }

    void Register(ClientModuleBuilder builder);
}
