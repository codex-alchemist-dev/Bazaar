using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Reports;

/// <summary>A state the system entered during the report period.</summary>
public sealed record ReportStateChange(DateTimeOffset At, KodoState State);

/// <summary>One health score sample.</summary>
public sealed record ReportScorePoint(DateTimeOffset At, int Score);

/// <summary>One SLO's standing (doc 46 section 12.3). Both figures are permille, so no floating point enters the report.</summary>
/// <param name="Id">The SLO id, a component id.</param>
/// <param name="TargetPermille">The target, from 0 to 1000 (995 is 99.5 percent).</param>
/// <param name="RemainingBudgetPermille">The error budget left, from 0 to 1000.</param>
public sealed record ReportSlo(string Id, int TargetPermille, int RemainingBudgetPermille);

/// <summary>An incident's summary facts. Identifiers and times only; the free text of a post-incident summary never comes in (doc 46 section 16.4).</summary>
public sealed record ReportIncident(string Id, string Category, AlertSeverity Severity, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt);

/// <summary>How many alerts were raised in the period, by severity.</summary>
public sealed record ReportAlertCounts(int Info, int Warning, int Critical);

/// <summary>
/// Everything the builder reads, gathered by the host from the evaluator, the score, the SLO tracker and the incident record. It is
/// data only; the builder does no I/O.
/// </summary>
/// <param name="Snapshot">The status snapshot at the end of the period.</param>
/// <param name="StateChanges">State changes, any order. Those outside the period are ignored.</param>
/// <param name="Scores">Score samples, any order. Those outside the period are ignored.</param>
/// <param name="Slos">SLO standings at the end of the period.</param>
/// <param name="Incidents">Incidents. Those that did not overlap the period are ignored.</param>
/// <param name="Alerts">Alert counts for the period.</param>
public sealed record ReportInputs(
    KodoStatusSnapshotDto Snapshot,
    IReadOnlyList<ReportStateChange> StateChanges,
    IReadOnlyList<ReportScorePoint> Scores,
    IReadOnlyList<ReportSlo> Slos,
    IReadOnlyList<ReportIncident> Incidents,
    ReportAlertCounts Alerts);

/// <summary>A state change in the report, with the wire name of the state.</summary>
public sealed record ReportStateEntry(DateTimeOffset At, string State);

/// <summary>A component that was not green at the end of the period. The cause is a code, never free text.</summary>
public sealed record ReportComponentEntry(string Id, string State, string Cause, DateTimeOffset Since);

/// <summary>An SLO line in the report.</summary>
public sealed record ReportSloEntry(string Id, int TargetPermille, int RemainingBudgetPermille);

/// <summary>An incident line in the report.</summary>
public sealed record ReportIncidentEntry(string Id, string Category, string Severity, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt);

/// <summary>
/// A generated health report (doc 46 section 16.7). Identifiers, numbers and times only: no model text, no free text, no names of
/// agents, GCs or content. It is the same bytes for the same inputs, so its digest can be checked and a signer can sign the digest.
/// </summary>
/// <param name="SchemaVersion">The shape version, <see cref="CurrentSchemaVersion"/>.</param>
/// <param name="Id">The report id: the definition id and the period end, so a re-run of one slot gives the same id.</param>
/// <param name="DefinitionId">The definition that produced it.</param>
/// <param name="Cadence">The cadence word, <c>daily</c> or <c>weekly</c>.</param>
/// <param name="PeriodStart">The start of the period, in UTC.</param>
/// <param name="PeriodEnd">The end of the period (the due instant), in UTC.</param>
/// <param name="GeneratedAt">When it was built, in UTC.</param>
/// <param name="State">The displayed state at the end of the period (wire word).</param>
/// <param name="UnderlyingState">The underlying state when frozen, otherwise null.</param>
/// <param name="Score">The score at the end of the period.</param>
/// <param name="ScoreMin">The lowest score sample in the period, or null with no samples.</param>
/// <param name="ScoreMax">The highest score sample, or null.</param>
/// <param name="ScoreMean">The mean score sample, rounded half up, or null.</param>
/// <param name="StateChanges">State changes in time order.</param>
/// <param name="Components">Components that were not green, by id.</param>
/// <param name="Slos">SLO standings, by id.</param>
/// <param name="Incidents">Incidents that overlapped the period, by opening time.</param>
/// <param name="Alerts">Alert counts.</param>
/// <param name="JournalHead">The lowercase hex journal head at the end of the period, or null.</param>
/// <param name="Omitted">Entries left out because an identifier was not acceptable or a list was over its cap. Never silent.</param>
public sealed record HealthReport(
    int SchemaVersion,
    string Id,
    string DefinitionId,
    string Cadence,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset GeneratedAt,
    string State,
    string? UnderlyingState,
    int Score,
    int? ScoreMin,
    int? ScoreMax,
    int? ScoreMean,
    IReadOnlyList<ReportStateEntry> StateChanges,
    IReadOnlyList<ReportComponentEntry> Components,
    IReadOnlyList<ReportSloEntry> Slos,
    IReadOnlyList<ReportIncidentEntry> Incidents,
    ReportAlertCounts Alerts,
    string? JournalHead,
    int Omitted)
{
    /// <summary>The shape version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;
}
