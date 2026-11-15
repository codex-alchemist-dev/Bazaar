namespace Bazaar.Kodo.Probes.Self;

/// <summary>
/// The state of the local journal spill file (doc 46 section 7.5).
/// </summary>
/// <param name="OldestUnreplayedUtc">When the oldest record still waiting for replay was written.</param>
/// <param name="Sealed">True once the spill has been replayed into Postgres and sealed for retention.</param>
public readonly record struct SpillFileReading(DateTimeOffset OldestUnreplayedUtc, bool Sealed);

/// <summary>
/// The reads behind the Kodo self probes (doc 46 section 9.1, <c>kodo.self.*</c>). Like <see cref="Host.IHostReadings"/>,
/// the core does no real I/O: the server supplies the implementation and tests fake it.
/// </summary>
/// <remarks>
/// An implementation throws when it cannot perform a read. The probe turns that into <c>unknown</c>, never green.
/// </remarks>
public interface IKodoSelfReadings
{
    /// <summary>
    /// Attempts a fresh write to the journal location. Returns false when the write is refused, and throws when the
    /// attempt itself cannot run.
    /// </summary>
    Task<bool> CanWriteJournalAsync(CancellationToken cancellationToken);

    /// <summary>The spill file state, or null when no spill file exists. Throws when the file cannot be read.</summary>
    Task<SpillFileReading?> ReadSpillFileAsync(CancellationToken cancellationToken);
}
