namespace Bazaar.Client.Commands;

/// <summary>
/// Routes a chosen command id to the handler a module registered for it. Call <see cref="TryInvoke"/> on the UI thread: the
/// handler runs on the caller's thread, and an exception from a handler reaches the caller.
/// </summary>
public sealed class CommandRouter : ICommandRouter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Action> _handlers = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IDisposable Register(string commandId, Action handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
        {
            if (!_handlers.TryAdd(commandId, handler))
            {
                throw new InvalidOperationException($"Command '{commandId}' already has a handler.");
            }
        }

        return new Registration(this, commandId, handler);
    }

    /// <summary>True when a handler is registered for the command.</summary>
    public bool IsHandled(string commandId)
    {
        lock (_gate)
        {
            return _handlers.ContainsKey(commandId);
        }
    }

    /// <summary>Runs the command's handler. Returns false, and runs nothing, when no module registered one.</summary>
    public bool TryInvoke(string commandId)
    {
        Action? handler;
        lock (_gate)
        {
            _handlers.TryGetValue(commandId, out handler);
        }

        if (handler is null)
        {
            return false;
        }

        handler();
        return true;
    }

    private void Remove(string commandId, Action handler)
    {
        lock (_gate)
        {
            if (_handlers.TryGetValue(commandId, out var current) && current == handler)
            {
                _handlers.Remove(commandId);
            }
        }
    }

    private sealed class Registration(CommandRouter owner, string commandId, Action handler) : IDisposable
    {
        public void Dispose() => owner.Remove(commandId, handler);
    }
}
