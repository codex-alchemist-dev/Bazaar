using Bazaar.Kodo.Export;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Incidents;

/// <summary>Where an incident stands (doc 46 section 16.3). The freeze holds in every phase except <see cref="Closed"/>.</summary>
public enum IncidentPhase
{
    /// <summary>The record exists; the freeze and the evidence snapshot are not complete yet.</summary>
    Opened,

    /// <summary>The freeze is applied and the evidence is sealed.</summary>
    Frozen,

    /// <summary>People are looking at the cause. No recovery action runs.</summary>
    Investigating,

    /// <summary>At least one of the incident's runbooks has been started.</summary>
    Recovering,

    /// <summary>The probes that opened the incident are green for the stability window. The freeze still holds until exit.</summary>
    Resolved,

    /// <summary>A role with the exit control confirmed, with step-up. The freeze is lifted. Final.</summary>
    Closed,
}

/// <summary>What opened an incident (section 16.3). Manual entry needs the enter control; the other four are the automatic floor.</summary>
public enum IncidentTrigger
{
    Manual,
    CriticalProbeRed,
    IntegrityFailure,
    SplitBrain,
    ReplicationBreak,
}

/// <summary>One change of phase: who, when and why (section 16.3 item 1). The reason is a short identifier code, never free text.</summary>
/// <param name="Index">Position in the timeline, starting at 0.</param>
/// <param name="From">The phase left.</param>
/// <param name="To">The phase entered.</param>
/// <param name="At">When.</param>
/// <param name="Actor">Who, as an identifier. <c>kodo</c> for the system.</param>
/// <param name="Reason">Why, as an identifier code.</param>
/// <param name="ApprovalId">The approval gate's id for transitions that need one, otherwise null.</param>
public sealed record IncidentTransition(int Index, IncidentPhase From, IncidentPhase To, DateTimeOffset At, string Actor, string Reason, string? ApprovalId);

/// <summary>A runbook the incident suggests. <paramref name="Available"/> is false when the catalogue does not hold a signed copy yet.</summary>
public sealed record PlannedRunbook(string RunbookId, int Version, string Title, bool Available);

/// <summary>A runbook run linked to the incident, with the phase last seen.</summary>
public sealed record IncidentRunLink(string RunId, string RunbookId, RunbookPhase Phase);

/// <summary>
/// The persisted incident state (section 16.3 item 1). Identifiers, codes and counters only. It carries everything needed to continue
/// after a restart, so every change is journalled first and saved second.
/// </summary>
public sealed record IncidentRecord(
    int Version,
    long Number,
    string Id,
    AlertSeverity Severity,
    IncidentTrigger Trigger,
    string Cause,
    string Title,
    string Opener,
    string OpenReason,
    DateTimeOffset OpenedAt,
    IncidentPhase Phase,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> LinkedAlerts,
    IReadOnlyList<PlannedRunbook> Plan,
    IReadOnlyList<IncidentRunLink> Runs,
    IReadOnlyList<IncidentTransition> Transitions,
    bool FreezeApplied,
    bool FreezeReleased,
    string? EvidenceRoot,
    int EvidenceItems,
    DateTimeOffset UpdatedAt,
    long Sequence)
{
    /// <summary>The state format version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The most transitions one incident keeps. The last two slots are kept for resolving and closing.</summary>
    public const int MaxTransitions = 128;

    /// <summary>The most affected categories.</summary>
    public const int MaxCategories = 8;

    /// <summary>The most linked alert fingerprints.</summary>
    public const int MaxLinkedAlerts = 16;

    /// <summary>The most planned runbooks.</summary>
    public const int MaxPlan = 16;

    /// <summary>The most linked runs.</summary>
    public const int MaxRuns = 32;

    /// <summary>The longest title.</summary>
    public const int MaxTitleLength = 120;

    /// <summary>True from opening until exit. While true the displayed state is frozen.</summary>
    public bool IsActive => Phase != IncidentPhase.Closed;

    /// <summary>The incident id for a number. Numbers are sequential per install and never reused.</summary>
    public static string IdFor(long number) => $"inc-{number:D6}";
}

/// <summary>The identifier checks shared by the incident code.</summary>
internal static class IncidentText
{
    public static bool IsIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) && KodoExportPatterns.IdentifierValue().IsMatch(value);

    public static bool IsComponent(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 && KodoExportPatterns.ComponentId().IsMatch(value);

    public static bool IsTitle(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= IncidentRecord.MaxTitleLength && value.All(c => c is >= ' ' and <= '~');
}
