namespace Bazaar.Kodo.Incidents;

/// <summary>
/// Persists incident state so a restart can continue (section 16.3 item 1). A save replaces the stored record only when its sequence is
/// higher; the implementation throws <see cref="IOException"/> when it cannot save or read.
/// </summary>
public interface IIncidentStore
{
    /// <summary>Takes the next incident number. Numbers are sequential per install and never reused, even when the incident is never saved.</summary>
    ValueTask<long> NextNumberAsync(CancellationToken cancellationToken);

    /// <summary>Loads an incident, or null when none exists.</summary>
    ValueTask<IncidentRecord?> LoadAsync(string incidentId, CancellationToken cancellationToken);

    /// <summary>Saves an incident.</summary>
    ValueTask SaveAsync(IncidentRecord record, CancellationToken cancellationToken);

    /// <summary>Every incident that is active, or closed with its freeze not yet released.</summary>
    ValueTask<IReadOnlyList<IncidentRecord>> ListUnsettledAsync(CancellationToken cancellationToken);
}

/// <summary>One incident journal entry. A short code in <see cref="Detail"/>, never free text.</summary>
public sealed record IncidentJournalEntry(string IncidentId, long Sequence, DateTimeOffset At, string Kind, string Actor, string Detail);

/// <summary>The incident journal kinds.</summary>
public static class IncidentJournalKinds
{
    public const string Opened = "incident_opened";
    public const string Transition = "incident_transition";
    public const string ApprovalDenied = "incident_approval_denied";
    public const string EvidenceSealed = "incident_evidence_sealed";
    public const string EvidenceFailed = "incident_evidence_failed";
    public const string RunLinked = "incident_run_linked";
    public const string RunUpdated = "incident_run_updated";
    public const string FreezeReleased = "incident_freeze_released";
}

/// <summary>
/// The journal seam. Every change is journalled before it is saved. The implementation throws <see cref="IOException"/> when it cannot
/// write; the change is then refused, never made without a record.
/// </summary>
public interface IIncidentJournal
{
    /// <summary>Appends one entry.</summary>
    ValueTask AppendAsync(IncidentJournalEntry entry, CancellationToken cancellationToken);
}

/// <summary>
/// Applies and lifts the freeze outside Kodo: holds automatic updates (doc 49), refuses config changes outside the allowlist, pauses agent
/// high-impact actions through doc 15's kill switch scope <see cref="IncidentFreeze.KillSwitchScope"/>, blocks module installs and chaos mode.
/// Both calls are idempotent. The implementation throws <see cref="IOException"/> when it cannot do it.
/// </summary>
public interface IIncidentFreezeSink
{
    /// <summary>Applies the freeze for an incident.</summary>
    ValueTask ApplyAsync(string incidentId, CancellationToken cancellationToken);

    /// <summary>Lifts the freeze for an incident.</summary>
    ValueTask ReleaseAsync(string incidentId, CancellationToken cancellationToken);
}
