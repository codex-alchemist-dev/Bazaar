using Bazaar.Client.Navigation.Abstractions;

namespace Bazaar.Client.Navigation;

/// <summary>
/// Runs commands that modules registered by id. Registering a second handler for an id throws. A command can be disabled
/// by its own predicate; the runner refuses it then, the same as the palette does.
/// </summary>
public sealed class CommandRunner : ICommandRunner
{
    private readonly Dictionary<string, (Func<CancellationToken, Task> Run, Func<bool>? Enabled)> _handlers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Register(string commandId, Func<CancellationToken, Task> handler, Func<bool>? enabled = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            if (!_handlers.TryAdd(commandId, (handler, enabled)))
            {
                throw new InvalidOperationException($"Command '{commandId}' already has a handler.");
            }
        }
    }

    public Task RunAsync(string commandId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        (Func<CancellationToken, Task> Run, Func<bool>? Enabled) entry;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(commandId, out entry))
            {
                return Task.FromException(new NavigationException($"No module registered the command '{commandId}'."));
            }
        }

        return entry.Enabled is { } enabled && !enabled()
            ? Task.FromException(new NavigationException($"The command '{commandId}' is not available right now."))
            : entry.Run(cancellationToken);
    }
}
