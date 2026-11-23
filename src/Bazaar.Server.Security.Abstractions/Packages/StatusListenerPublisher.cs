using System.Collections.Immutable;

namespace Bazaar.Server.Security.Packages;

/// <summary>
/// In-memory fan-out to status listeners. Listeners run in subscription order on the caller's thread. A listener that
/// throws is reported through the callback and the others still run, so one bad listener cannot hide a status change.
/// </summary>
/// <typeparam name="TListener">Listener contract.</typeparam>
/// <param name="onListenerError">Receives the listener type name and the exception. Usually a logger call.</param>
public sealed class StatusListenerPublisher<TListener>(Action<string, Exception> onListenerError)
    where TListener : class
{
    private readonly Action<string, Exception> _onListenerError = onListenerError ?? throw new ArgumentNullException(nameof(onListenerError));
    private readonly object _gate = new();
    private ImmutableArray<TListener> _listeners = [];

    /// <summary>Adds a listener at the end of the order. Listeners added during a publish are seen from the next publish.</summary>
    public void Subscribe(TListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            _listeners = _listeners.Add(listener);
        }
    }

    /// <summary>Calls <paramref name="notify"/> on every listener, in subscription order, isolating failures.</summary>
    public void Publish(Action<TListener> notify)
    {
        ArgumentNullException.ThrowIfNull(notify);

        ImmutableArray<TListener> snapshot;
        lock (_gate)
        {
            snapshot = _listeners;
        }

        foreach (var listener in snapshot)
        {
            try
            {
                notify(listener);
            }
            catch (Exception ex)
            {
                _onListenerError(listener.GetType().FullName ?? typeof(TListener).Name, ex);
            }
        }
    }
}
