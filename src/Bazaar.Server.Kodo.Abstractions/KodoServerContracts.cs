using Bazaar.Contracts.Kodo;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The latest Kodo status snapshot the server holds (doc 46 section 15.7). Readers get the snapshot as it was last published, or
/// null while nothing has been published yet; they never see a half-written one.
/// </summary>
public interface IKodoSnapshotProvider
{
    /// <summary>The latest published snapshot, or null when none has been published since the server started.</summary>
    KodoStatusSnapshotDto? Current { get; }
}

/// <summary>
/// The feed side of the holder. The evaluator host builds a snapshot with <c>KodoStatusSnapshotBuilder</c> and publishes it here.
/// </summary>
public interface IKodoSnapshotPublisher
{
    /// <summary>
    /// Replaces the held snapshot when <paramref name="snapshot"/> is not older than it: a lower revision is dropped, and an equal
    /// revision wins only with a later <c>AsOf</c>. Returns whether the snapshot was taken. Safe to call from many threads.
    /// </summary>
    bool Publish(KodoStatusSnapshotDto snapshot);
}

/// <summary>
/// One open incident as a reader may show it: a title and a runbook link, never evidence, logs or payloads (doc 46 sections 15.1
/// and 16).
/// </summary>
/// <param name="Id">The incident id.</param>
/// <param name="Title">A short human title. No raw log text.</param>
/// <param name="Severity">The severity word, for example <c>critical</c> or <c>warning</c>.</param>
/// <param name="OpenedAt">When the incident opened, in UTC.</param>
/// <param name="RunbookLink">A link to the runbook, or null when the incident has none.</param>
public sealed record KodoIncidentSummary(string Id, string Title, string Severity, DateTimeOffset OpenedAt, string? RunbookLink);

/// <summary>
/// Where open incidents come from. The incident record (doc 46 section 16.3) is not built yet, so the shipped default reports none;
/// the incident module replaces it when it lands.
/// </summary>
public interface IKodoIncidentSource
{
    /// <summary>Lists the open incidents, in any order.</summary>
    Task<IReadOnlyList<KodoIncidentSummary>> ListOpenAsync(CancellationToken ct = default);
}
