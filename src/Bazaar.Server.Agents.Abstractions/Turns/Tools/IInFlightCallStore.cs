namespace Bazaar.Server.Agents.Turns.Tools;

/// <summary>
/// Marks a tool call as started before it runs and as finished after it returns (doc 06, crash recovery). A marker
/// still open after a crash means the call's outcome is unknown.
/// </summary>
public interface IInFlightCallStore
{
    /// <summary>True when a call with this key was begun and never completed.</summary>
    Task<bool> HasUnfinishedAsync(string callKey, CancellationToken cancellationToken);

    /// <summary>Records that the call is about to run.</summary>
    Task BeginAsync(string callKey, CancellationToken cancellationToken);

    /// <summary>Records that the call returned, with a result or a converted failure.</summary>
    Task CompleteAsync(string callKey, CancellationToken cancellationToken);
}
