namespace Bazaar.Ex;

/// <summary>How a dispatched command ended.</summary>
/// <param name="Handled">True when a registered handler took the command id.</param>
/// <param name="Notice">A line to show or announce, or null when the command needs none.</param>
public sealed record CommandDispatchResult(bool Handled, string? Notice);

/// <summary>
/// Routes a command id (from a mnemonic or the palette) to the module that owns it. Handlers register by predicate, so a
/// module's own "is this mine" check is used. The first handler that claims the id runs it. An id nobody claims is reported in
/// the result, never thrown, so a module that has no handler yet changes nothing. Pure: no UI types.
/// </summary>
public sealed class ClientCommandDispatcher
{
    private readonly List<(Func<string, bool> Handles, Func<string, CancellationToken, Task<string?>> Invoke)> _handlers = [];

    /// <summary>Registers a handler. <paramref name="invoke"/> returns a notice for the user, or null when it has nothing to say.</summary>
    public void Add(Func<string, bool> handles, Func<string, CancellationToken, Task<string?>> invoke)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(invoke);
        _handlers.Add((handles, invoke));
    }

    public async Task<CommandDispatchResult> DispatchAsync(string commandId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        foreach (var (handles, invoke) in _handlers)
        {
            if (!handles(commandId))
            {
                continue;
            }

            try
            {
                return new CommandDispatchResult(true, await invoke(commandId, cancellationToken).ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                return new CommandDispatchResult(true, $"The command failed: {ex.Message}");
            }
        }

        return new CommandDispatchResult(false, $"No handler is available for '{commandId}' in this client.");
    }
}
