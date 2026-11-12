using System.Globalization;
using Bazaar.Server.Kanban.Data;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// One event as the event bridge sees it (doc 38, WP-R6). <see cref="Kind"/> is a closed-set predicate wire name, so it matches
/// a predicate's <see cref="RyuseiPredicate.Kind"/> exactly. <see cref="ProjectId"/> is null for GC-sourced events, because the
/// <c>gc</c> table has no project column; for those the predicate must name its GC (see <see cref="RyuseiEventRules.IsInScope"/>).
/// </summary>
public sealed record RyuseiEventEnvelope(
    string Kind,
    Guid? ProjectId,
    Guid? TaskId,
    Guid? GcId,
    Guid? AuthorAgentId,
    string? VaultPath,
    string PayloadJson)
{
    /// <summary>The fields the predicate matcher reads (doc 38, closed predicate set).</summary>
    public RyuseiEvent ToMatchEvent() => new()
    {
        Kind = Kind,
        GcId = GcId,
        AuthorAgentId = AuthorAgentId,
        VaultPath = VaultPath,
    };
}

/// <summary>
/// Maps the events the repo already writes to closed-set predicate kinds (doc 38, Layer 2 closed predicate set). An event
/// that is not in the set maps to null and is never matched. Kinds with no source in the repo yet (PR events, Vault changes)
/// have no mapping here; see the WP-R6 report.
/// </summary>
public static class RyuseiEventMapping
{
    /// <summary>Maps a <c>task_events.kind</c> (doc 12). Created, edge, guard, gate and review events are not in the closed set.</summary>
    public static RyuseiEventEnvelope? FromTaskEvent(string taskEventKind, Guid projectId, Guid taskId, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskEventKind);
        ArgumentNullException.ThrowIfNull(payloadJson);

        var predicate = taskEventKind switch
        {
            TaskEventKinds.Transitioned => RyuseiPredicates.WireName(RyuseiPredicateKind.TaskTransitioned),
            TaskEventKinds.DuePassed => RyuseiPredicates.WireName(RyuseiPredicateKind.TaskDuePassed),
            TaskEventKinds.Blocked => RyuseiPredicates.WireName(RyuseiPredicateKind.TaskBlocked),
            TaskEventKinds.Unblocked => RyuseiPredicates.WireName(RyuseiPredicateKind.TaskUnblocked),
            _ => null,
        };

        return predicate is null ? null : new RyuseiEventEnvelope(predicate, projectId, taskId, null, null, null, payloadJson);
    }

    /// <summary>
    /// Maps one GC event. A <c>message</c> is a new message (the first version); an edit is not. Council decisions (doc 10) are
    /// GC events of kind <c>council.decision</c>. <paramref name="authorKind"/> is the <c>gc_event.author_kind</c>.
    /// </summary>
    public static RyuseiEventEnvelope? FromGcEvent(string gcEventKind, Guid gcId, string authorKind, Guid authorId, long seq)
    {
        ArgumentException.ThrowIfNullOrEmpty(gcEventKind);
        ArgumentException.ThrowIfNullOrEmpty(authorKind);
        var payload = $"{{\"seq\":{seq.ToString(CultureInfo.InvariantCulture)}}}";

        switch (gcEventKind)
        {
            case "message":
                // Only an agent author is an agent id. Owner and human authors carry no AuthorAgentId.
                var agentAuthor = string.Equals(authorKind, "agent", StringComparison.Ordinal) ? authorId : (Guid?)null;
                return new RyuseiEventEnvelope(
                    RyuseiPredicates.WireName(RyuseiPredicateKind.GcMessage),
                    ProjectId: null,
                    TaskId: null,
                    GcId: gcId,
                    AuthorAgentId: agentAuthor,
                    VaultPath: null,
                    PayloadJson: payload);

            case "council.decision":
                return new RyuseiEventEnvelope(
                    RyuseiPredicates.WireName(RyuseiPredicateKind.CouncilDecisionRecorded),
                    ProjectId: null,
                    TaskId: null,
                    GcId: gcId,
                    AuthorAgentId: null,
                    VaultPath: null,
                    PayloadJson: payload);

            default:
                return null;
        }
    }
}
