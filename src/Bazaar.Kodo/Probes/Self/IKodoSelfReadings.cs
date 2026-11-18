namespace Bazaar.Kodo.Probes.Self;

/// <summary>
/// The state of the local journal spill file (doc 46 section 7.5).
/// </summary>
/// <param name="OldestUnsentUtc">When the oldest record that has not yet been sent to Postgres was written.</param>
/// <param name="Sealed">True when the spill is sealed. A sealed spill with records still waiting is red (section 7.5).</param>
public readonly record struct SpillFileReading(DateTimeOffset OldestUnsentUtc, bool Sealed);

/// <summary>
/// The reads behind the Kodo self probes (doc 46 section 9.1, <c>kodo.self.*</c>). Like <see cref="Host.IHostReadings"/>,
/// the core does no real I/O: the server supplies the implementation and tests fake it.
/// </summary>
/// <remarks>
/// An implementation throws when it cannot perform a read. What the probe does with that depends on the probe:
/// <c>kodo.self.journal_writable</c> reports unknown, and <c>kodo.self.spill</c> reports red.
/// </remarks>
public interface IKodoSelfReadings
{
    /// <summary>
    /// Attempts an append and an fsync through the journal writer. Returns false when the write is refused, and throws
    /// when the attempt itself cannot run.
    /// </summary>
    Task<bool> CanWriteJournalAsync(CancellationToken cancellationToken);

    /// <summary>The spill file state, or null when no spill file exists. Throws when the file cannot be read.</summary>
    Task<SpillFileReading?> ReadSpillFileAsync(CancellationToken cancellationToken);
}
