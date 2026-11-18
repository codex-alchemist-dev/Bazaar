namespace Bazaar.Client.OfflineSync;

/// <summary>The arguments of <see cref="ConnectionStateTracker.StateChanged"/>.</summary>
/// <param name="previous">The state before the change.</param>
/// <param name="current">The state after the change.</param>
public sealed class ConnectionStateChangedEventArgs(ConnectionState previous, ConnectionState current) : EventArgs
{
    /// <summary>The state before the change.</summary>
    public ConnectionState Previous { get; } = previous;

    /// <summary>The state after the change.</summary>
    public ConnectionState Current { get; } = current;
}

/// <summary>
/// Holds the current <see cref="ConnectionState"/> and raises <see cref="StateChanged"/> on each real change. Thread safe.
/// Events are raised after the lock is released, so under concurrent updates two handlers may see changes out of order.
/// </summary>
public sealed class ConnectionStateTracker
{
    private readonly Lock _gate = new();
    private ConnectionState _current = new ConnectionState.Live();

    /// <summary>Raised when the state changes to a different value.</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>The current state.</summary>
    public ConnectionState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Sets the state. Setting an equal state does nothing and raises no event.</summary>
    public void Set(ConnectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ConnectionStateChangedEventArgs args;
        lock (_gate)
        {
            if (_current == state)
            {
                return;
            }

            args = new ConnectionStateChangedEventArgs(_current, state);
            _current = state;
        }

        StateChanged?.Invoke(this, args);
    }
}
