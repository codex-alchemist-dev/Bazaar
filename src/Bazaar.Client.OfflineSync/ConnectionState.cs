namespace Bazaar.Client.OfflineSync;

/// <summary>The connection chip state (doc 56 section 6, doc 27). A closed set of nested records.</summary>
public abstract record ConnectionState
{
    /// <summary>Connected, with nothing pending.</summary>
    public sealed record Live : ConnectionState;

    /// <summary>Offline. The queue holds the pending writes.</summary>
    /// <param name="AsOf">When the connection was last known to be good.</param>
    public sealed record Offline(DateTimeOffset AsOf) : ConnectionState;

    /// <summary>Connected and sending pending writes.</summary>
    /// <param name="Done">Writes acknowledged in this run.</param>
    /// <param name="Total">Writes pending when the run started.</param>
    public sealed record Syncing(long Done, long Total) : ConnectionState;

    /// <summary>Connected, but some writes need a person (conflict, held for review, or rejected).</summary>
    /// <param name="Count">Writes that need attention.</param>
    public sealed record Attention(int Count) : ConnectionState;
}
