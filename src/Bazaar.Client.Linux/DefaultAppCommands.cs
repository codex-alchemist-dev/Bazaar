using Bazaar.Client.Commands;
using Bazaar.Client.DefaultApp.Linux;

namespace Bazaar.Client.Linux;

/// <summary>
/// The local palette commands for default-app registration (doc 30 section 1). <see cref="All"/> is what a palette lists;
/// <see cref="RunAsync"/> is what each runs and returns the honest status text. Nothing here claims a silent default.
/// </summary>
public static class DefaultAppCommands
{
    /// <summary>Id of the register command.</summary>
    public const string RegisterId = "defaultapp.register";

    /// <summary>Id of the status command.</summary>
    public const string StatusId = "defaultapp.status";

    /// <summary>Id of the unregister command.</summary>
    public const string UnregisterId = "defaultapp.unregister";

    /// <summary>The palette entries: register, status and unregister. Local commands.</summary>
    public static IReadOnlyList<Command> All { get; } =
    [
        new(RegisterId, "Register Bazaar as a handler", "register default app", ["default", "handler", "xdg", "scheme"], CommandSource.Local),
        new(StatusId, "Show default-app status", "default app status", ["default", "handler", "xdg"], CommandSource.Local),
        new(UnregisterId, "Remove the Bazaar handler registration", "unregister default app", ["default", "handler", "remove"], CommandSource.Local),
    ];

    /// <summary>
    /// Runs the command with <paramref name="id"/> through <paramref name="registrar"/> and returns the status text, or null
    /// when <paramref name="id"/> is not a default-app command.
    /// </summary>
    public static async Task<string?> RunAsync(string id, IDefaultAppRegistrar registrar, CancellationToken ct = default)
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
