namespace Bazaar.Tabula;

/// <summary>A chain head to publish outside the machine (doc 85 R-16): enough for a verifier to detect a chain cut short at the tail.</summary>
public sealed record AnchorCheckpoint(ChainId Chain, long Seq, byte[] HeadHash, DateTimeOffset At);

/// <summary>
/// Destination for chain checkpoints (a git repository, an RFC 3161 time-stamp authority, a file). Implementations are
/// provider modules in the host, never in this library.
/// </summary>
public interface IAnchorSink
{
    /// <summary>Stable name of the sink, for reports.</summary>
    string Name { get; }

    /// <summary>Publishes the checkpoint. Completes when the sink has accepted it.</summary>
    Task AnchorAsync(AnchorCheckpoint checkpoint, CancellationToken ct = default);
}
