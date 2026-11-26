using Bazaar.Client.Commands;
using Bazaar.Client.DefaultApp.Linux;

namespace Bazaar.Client.Linux;

/// <summary>
/// The local palette commands for default-terminal registration (doc 30 section 1). <see cref="All"/> is what a palette lists;
/// <see cref="RunAsync"/> runs one and returns the honest status text. Nothing here claims a silent default.
/// </summary>
public static class DefaultTerminalCommands
{
    /// <summary>Id of the register command.</summary>
    public const string RegisterId = "defaultterminal.register";

    /// <summary>Id of the status command.</summary>
    public const string StatusId = "defaultterminal.status";

    /// <summary>Id of the unregister command.</summary>
    public const string UnregisterId = "defaultterminal.unregister";

    /// <summary>The palette entries: register, status and unregister. Local commands.</summary>
    public static IReadOnlyList<Command> All { get; } =
    [
        new(RegisterId, "Set Bazaar Terminal as my default terminal", "register default terminal", ["default", "terminal", "xdg"], CommandSource.Local),
        new(StatusId, "Show default-terminal status", "default terminal status", ["default", "terminal", "xdg"], CommandSource.Local),
        new(UnregisterId, "Remove the Bazaar Terminal registration", "unregister default terminal", ["default", "terminal", "remove"], CommandSource.Local),
    ];

    /// <summary>
    /// Runs the command with <paramref name="id"/> through <paramref name="registrar"/> and returns the status text, or null
    /// when <paramref name="id"/> is not a default-terminal command.
    /// </summary>
    public static async Task<string?> RunAsync(string id, IDefaultTerminalRegistrar registrar, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(registrar);
        var pending = id switch
        {
            RegisterId => registrar.RegisterAsync(ct),
            StatusId => registrar.QueryAsync(ct),
            UnregisterId => registrar.UnregisterAsync(ct),
            _ => null,
        };
        if (pending is null)
        {
            return null;
        }

        var writer = new StringWriter();
        DefaultAppCommandLine.Print(await pending.ConfigureAwait(false), writer);
        return writer.ToString().TrimEnd();
    }
}
