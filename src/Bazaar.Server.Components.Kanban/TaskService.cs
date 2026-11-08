using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Kanban.Data;
using Npgsql;

namespace Bazaar.Server.Kanban;

/// <summary>
/// The only writer of task state, edges and gates (doc 12). Each change runs in one transaction with its
/// <c>task_events</c> rows, so an event exists only when its change committed.
/// <para>
/// Locking: a transition takes the project's graph lock shared, or exclusive when it moves a task to Done (that can auto-ready
/// dependents). Edge changes take it exclusive. Rows are locked after the graph lock, the task first and then its blockers
/// in ascending id order, so the only lock waits are ones that cannot form a cycle.
/// </para>
/// </summary>
public sealed class TaskService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TaskServiceOptions _options;
    private readonly TaskRepository _tasks;
    private readonly TaskEdgeRepository _edges;
    private readonly TaskLinkRepository _links;
    private readonly TaskEventRepository _events;
    private readonly TaskGateRepository _gates;
    private readonly IGcLogWriter? _gcLog;
    private readonly IGcExistence _gcExistence;

    /// <summary>
    /// Creates the service. <paramref name="gcLog"/> carries the reason lines (doc 12, WP6): without it, transitions and
    /// pins still work and post nothing to a GC. <paramref name="gcExistence"/> is how GC links are checked; null uses the
    /// <c>gc</c> table.
    /// </summary>
    public TaskService(NpgsqlDataSource dataSource, TaskServiceOptions? options = null, IGcLogWriter? gcLog = null, IGcExistence? gcExistence = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _gcLog = gcLog;
        _options = options ?? new TaskServiceOptions();
        _tasks = new TaskRepository(dataSource);
        _edges = new TaskEdgeRepository(dataSource);
        _links = new TaskLinkRepository(dataSource);
        _events = new TaskEventRepository(dataSource);
        _gates = new TaskGateRepository(dataSource);
        _gcExistence = gcExistence ?? new SqlGcExistence(dataSource);
    }

    /// <summary>
    /// Moves a task to <paramref name="to"/> if <paramref name="expectedVersion"/> still matches, the table allows the move
    /// for this actor, and no blocker is unfinished when starting work. A refused start is recorded as a
    /// <c>task.guard_refused</c> event and changes nothing else.
    /// </summary>
    public Task<TransitionResult> TransitionAsync(
        Guid taskId, TaskState to, TaskActor actor, string? reason, long expectedVersion, CancellationToken ct = default) =>
        TransitionCoreAsync(taskId, to, actor, reason, expectedVersion, prMerged: false, ct);

    /// <summary>The transition path shared by <see cref="TransitionAsync"/> and the PR-merge hook (WP12).</summary>
    private async Task<TransitionResult> TransitionCoreAsync(
        Guid taskId, TaskState to, TaskActor actor, string? reason, long expectedVersion, bool prMerged, CancellationToken ct)
    {
        // Only the PR-merge hook (WP12) may say a PR is merged; a caller's word is not enough.
        if (prMerged && actor.Kind != TaskActorKind.System)
        {
            throw new ArgumentException("A merged-PR move must come from the system actor.", nameof(actor));
        }

        var seen = await _tasks.GetAsync(taskId, ct).ConfigureAwait(false);
        if (seen is null)
        {
            return TransitionResult.Invalid(TransitionOutcome.UnknownTask);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockGraphAsync(tx, seen.ProjectId, exclusive: to == TaskState.Done, ct).ConfigureAwait(false);

        var task = await _tasks.GetForUpdateAsync(tx, taskId, ct).ConfigureAwait(false);
        if (task is null)
        {
            return TransitionResult.Invalid(TransitionOutcome.UnknownTask);
        }

        if (task.Version != expectedVersion)
        {
            return TransitionResult.Conflict(task.Version);
        }

        var incoming = await _edges.ListBlockersAsync(taskId, tx, ct).ConfigureAwait(false);
        var blockers = await LockRowsAsync(tx, incoming.Select(e => e.BlockerId), ct).ConfigureAwait(false);
        var blockerStates = blockers.ToDictionary(pair => pair.Key, pair => pair.Value.State);

        if (task.State == TaskState.Ready && to == TaskState.InProgress)
        {
            var start = BlockedStatus.CheckStart(taskId, incoming, blockerStates);
            if (!start.Allowed)
            {
                await _events.AppendAsync(tx, new NewTaskEvent(taskId, TaskEventKinds.GuardRefused, actor.ToText(),
                    Payload(("to", KanbanText.ToSql(to)), ("blockers", string.Join(",", start.UnfinishedBlockers)))), ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return TransitionResult.Refused(TransitionOutcome.BlockersUnfinished, start.UnfinishedBlockers);
            }
        }

        var links = await _links.ListAsync(taskId, tx, ct).ConfigureAwait(false);
        var gateOutcome = task.Gate == TaskGate.None ? null : await _gates.LatestAsync(taskId, task.Gate, tx, ct).ConfigureAwait(false);
        var context = new TransitionContext
        {
            IsSystem = actor.Kind == TaskActorKind.System,
            IsOwner = actor.Kind == TaskActorKind.Owner,
            IsAgent = actor.Kind == TaskActorKind.Agent,
            IsAssignee = actor.Kind == TaskActorKind.Agent && actor.Id == task.OwnerAgentId,
            IsReviewer = actor.Kind == TaskActorKind.Owner || (actor.Kind == TaskActorKind.Agent && actor.Id != task.OwnerAgentId),
            HasReason = !string.IsNullOrWhiteSpace(reason),
            ReviewLinks = links.Count(l => l.Kind is TaskLinkKind.Pr or TaskLinkKind.Commit),
            MinReviewLinks = _options.ReviewMinLinks,
            ReviewerUngatedDone = _options.ReviewerUngatedDone,
            Gate = task.Gate,
            // The latest recorded decision for the task's gate decides it (doc 12, WP9). No actor can pass a gate by asserting it.
            GateSatisfied = gateOutcome == TaskGateOutcome.Approved,
            HasPrLink = links.Any(l => l.Kind == TaskLinkKind.Pr),
            // Only the PR-merge hook sets this (see PrMergedAsync); a manual move cannot reach Done for a PR card.
            PrMerged = prMerged,
        };

        var outcome = TaskTransitionRules.Evaluate(task.State, to, context);
        if (outcome != TransitionOutcome.Allowed)
        {
            return outcome == TransitionOutcome.NotATransition
                ? TransitionResult.Invalid(outcome)
                : TransitionResult.Refused(outcome, []);
        }

        var owner = task.OwnerAgentId;
        if (to == TaskState.InProgress && owner is null && actor.Kind == TaskActorKind.Agent)
        {
            owner = actor.Id;
        }

        var graphBefore = await _edges.ListForProjectAsync(seen.ProjectId, tx, ct).ConfigureAwait(false);
        var statesBefore = await StatesAsync(tx, seen.ProjectId, ct).ConfigureAwait(false);
        var statesAfter = new Dictionary<Guid, TaskState>(statesBefore) { [taskId] = to };

        var next = task with { State = to, OwnerAgentId = owner };
        if (!await _tasks.UpdateAsync(tx, next, task.Version, ct).ConfigureAwait(false))
        {
            return TransitionResult.Conflict(task.Version);
        }

        var transitionSeq = await _events.AppendAsync(tx, new NewTaskEvent(taskId, TaskEventKinds.Transitioned, actor.ToText(),
            Payload(("from", KanbanText.ToSql(task.State)), ("to", KanbanText.ToSql(to)), ("reason", reason))), ct).ConfigureAwait(false);
        var reasonLines = new List<ReasonLine>
        {
            new(taskId, task.OriginGcId, transitionSeq, task.Title, KanbanText.ToSql(task.State), KanbanText.ToSql(to), reason),
        };

        var dependents = graphBefore.Where(e => e.BlockerId == taskId).Select(e => e.BlockedId);
        await EmitBlockedChangesAsync(tx, dependents, graphBefore, graphBefore, statesBefore, statesAfter, actor, ct).ConfigureAwait(false);

        if (to == TaskState.Done && _options.AutoReady)
        {
            foreach (var dependentId in BlockedStatus.DependentsBecomingReady(taskId, graphBefore, statesAfter, autoReadyEnabled: true))
            {
                var dependent = await _tasks.GetForUpdateAsync(tx, dependentId, ct).ConfigureAwait(false);
                if (dependent is null || dependent.State != TaskState.Backlog)
                {
                    continue;
                }

                await _tasks.UpdateAsync(tx, dependent with { State = TaskState.Ready }, dependent.Version, ct).ConfigureAwait(false);
                var readySeq = await _events.AppendAsync(tx, new NewTaskEvent(dependentId, TaskEventKinds.Transitioned, TaskActor.SystemActor.ToText(),
                    Payload(("from", "backlog"), ("to", "ready"), ("reason", "auto-ready: blockers done"))), ct).ConfigureAwait(false);
                reasonLines.Add(new ReasonLine(dependentId, dependent.OriginGcId, readySeq, dependent.Title, "backlog", "ready", "auto-ready: blockers done"));
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        await PostReasonLinesAsync(reasonLines, actor, ct).ConfigureAwait(false);
        return TransitionResult.Applied(task.Version + 1);
    }

    /// <summary>
    /// Adds "<paramref name="blockerId"/> blocks <paramref name="blockedId"/>". Runs under the exclusive graph lock, so two
    /// concurrent inserts that would close a cycle cannot both pass.
    /// </summary>
    public async Task<EdgeResult> AddBlockerAsync(Guid blockedId, Guid blockerId, TaskActor actor, CancellationToken ct = default)
    {
        var blocked = await _tasks.GetAsync(blockedId, ct).ConfigureAwait(false);
        var blocker = await _tasks.GetAsync(blockerId, ct).ConfigureAwait(false);
        if (blocked is null || blocker is null)
        {
            return EdgeResult.Of(EdgeStatus.NotFound);
        }

        if (blocked.ProjectId != blocker.ProjectId)
        {
            return EdgeResult.Of(EdgeStatus.CrossProject);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockGraphAsync(tx, blocked.ProjectId, exclusive: true, ct).ConfigureAwait(false);
        await LockRowsAsync(tx, [blockedId, blockerId], ct).ConfigureAwait(false);

        var graphBefore = await _edges.ListForProjectAsync(blocked.ProjectId, tx, ct).ConfigureAwait(false);
        var candidate = new TaskEdge(blockerId, blockedId);
        var check = BlockerGraph.CheckNewEdge(graphBefore, candidate, _options.MaxBlockersPerTask);
        if (check.Outcome != EdgeCheckOutcome.Allowed)
        {
            return EdgeResult.Of(check.Outcome switch
            {
                EdgeCheckOutcome.SelfEdge => EdgeStatus.SelfEdge,
                EdgeCheckOutcome.Duplicate => EdgeStatus.Duplicate,
                EdgeCheckOutcome.Cycle => EdgeStatus.Cycle,
                _ => EdgeStatus.TooManyBlockers,
            }, check.CyclePath);
        }

        var statesBefore = await StatesAsync(tx, blocked.ProjectId, ct).ConfigureAwait(false);
        await _edges.InsertAsync(tx, candidate, actor.Id, ct).ConfigureAwait(false);
        var graphAfter = graphBefore.Append(candidate).ToList();
        await _events.AppendAsync(tx, new NewTaskEvent(blockedId, TaskEventKinds.EdgeChanged, actor.ToText(),
            Payload(("action", "added"), ("blocker", blockerId.ToString("D")))), ct).ConfigureAwait(false);
        await EmitBlockedChangesAsync(tx, [blockedId], graphBefore, graphAfter, statesBefore, statesBefore, actor, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return EdgeResult.Of(EdgeStatus.Added);
    }

    /// <summary>Removes an edge. Takes the same exclusive graph lock as adding one.</summary>
    public async Task<EdgeResult> RemoveBlockerAsync(Guid blockedId, Guid blockerId, TaskActor actor, CancellationToken ct = default)
    {
        var blocked = await _tasks.GetAsync(blockedId, ct).ConfigureAwait(false);
        if (blocked is null)
        {
            return EdgeResult.Of(EdgeStatus.NotFound);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockGraphAsync(tx, blocked.ProjectId, exclusive: true, ct).ConfigureAwait(false);
        await LockRowsAsync(tx, [blockedId, blockerId], ct).ConfigureAwait(false);

        var candidate = new TaskEdge(blockerId, blockedId);
        var graphBefore = await _edges.ListForProjectAsync(blocked.ProjectId, tx, ct).ConfigureAwait(false);
        if (!graphBefore.Contains(candidate))
        {
            return EdgeResult.Of(EdgeStatus.NotPresent);
        }

        var statesBefore = await StatesAsync(tx, blocked.ProjectId, ct).ConfigureAwait(false);
        await _edges.DeleteAsync(tx, candidate, ct).ConfigureAwait(false);
        var graphAfter = graphBefore.Where(e => e != candidate).ToList();
        await _events.AppendAsync(tx, new NewTaskEvent(blockedId, TaskEventKinds.EdgeChanged, actor.ToText(),
            Payload(("action", "removed"), ("blocker", blockerId.ToString("D")))), ct).ConfigureAwait(false);
        await EmitBlockedChangesAsync(tx, [blockedId], graphBefore, graphAfter, statesBefore, statesBefore, actor, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return EdgeResult.Of(EdgeStatus.Removed);
    }

    /// <summary>
    /// Records a gate decision (doc 12, WP9). Owner gates are decided by the owner; council gates by the council hook, which
    /// passes the system actor and the council decision id. Recording changes no state; the next Review to Done reads it.
    /// </summary>
    public async Task<TransitionResult> RecordGateDecisionAsync(
        Guid taskId, TaskGateOutcome outcome, TaskActor actor, string reference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var seen = await _tasks.GetAsync(taskId, ct).ConfigureAwait(false);
        if (seen is null)
        {
            return TransitionResult.Invalid(TransitionOutcome.UnknownTask);
        }

        if (seen.Gate == TaskGate.None)
        {
            return TransitionResult.Invalid(TransitionOutcome.NotATransition);
        }

        var authorised = seen.Gate == TaskGate.Owner
            ? actor.Kind == TaskActorKind.Owner
            : actor.Kind == TaskActorKind.System;
        if (!authorised)
        {
            return TransitionResult.Refused(TransitionOutcome.NotAuthorised, []);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockGraphAsync(tx, seen.ProjectId, exclusive: false, ct).ConfigureAwait(false);
        var task = await _tasks.GetForUpdateAsync(tx, taskId, ct).ConfigureAwait(false);
        if (task is null)
        {
            return TransitionResult.Invalid(TransitionOutcome.UnknownTask);
        }

        await _gates.InsertAsync(tx, taskId, task.Gate, outcome, actor.Id, reference, ct).ConfigureAwait(false);
        await _events.AppendAsync(tx, new NewTaskEvent(taskId, TaskEventKinds.GateRecorded, actor.ToText(),
            Payload(("gate", KanbanText.ToSql(task.Gate)), ("outcome", KanbanText.ToSql(outcome)), ("reference", reference))), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return TransitionResult.Applied(task.Version);
    }

    /// <summary>
    /// Sets the reviewer-approval flag on a card in Review (doc 12, PR cards). The flag moves nothing: Done still needs the
    /// merge (for PR cards) or the normal rules. The reviewer is the owner or an agent that is not the assignee.
    /// </summary>
    public async Task<TransitionResult> RecordReviewApprovalAsync(Guid taskId, TaskActor reviewer, CancellationToken ct = default)
    {
        var seen = await _tasks.GetAsync(taskId, ct).ConfigureAwait(false);
        if (seen is null)
        {
            return TransitionResult.Invalid(TransitionOutcome.UnknownTask);
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await LockGraphAsync(tx, seen.ProjectId, exclusive: false, ct).ConfigureAwait(false);
        var task = await _tasks.GetForUpdateAsync(tx, taskId, ct).ConfigureAwait(false);
        if (task is null)
        {
            return TransitionResult.Invalid(TransitionOutcome.UnknownTask);
        }

        if (task.State != TaskState.Review)
        {
            return TransitionResult.Invalid(TransitionOutcome.NotATransition);
        }

        var isReviewer = reviewer.Kind == TaskActorKind.Owner
            || (reviewer.Kind == TaskActorKind.Agent && reviewer.Id != task.OwnerAgentId);
        if (!isReviewer)
        {
            return TransitionResult.Refused(TransitionOutcome.NotAuthorised, []);
        }

        var approvedAt = DateTimeOffset.UtcNow;
        if (!await _tasks.UpdateAsync(tx, task with { ReviewApprovedBy = reviewer.Id, ReviewApprovedAt = approvedAt }, task.Version, ct).ConfigureAwait(false))
        {
            return TransitionResult.Conflict(task.Version);
        }

        await _events.AppendAsync(tx, new NewTaskEvent(taskId, TaskEventKinds.ReviewApproved, reviewer.ToText(),
            Payload(("approved_at", approvedAt.ToString("O")))), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return TransitionResult.Applied(task.Version + 1);
    }

    /// <summary>
    /// Consumes a merged-PR event (doc 21's <c>pr_event</c>, WP12). Every card linked to <paramref name="prRef"/> that is in
    /// Review moves to Done through the normal transition path with the merge flag set. A PR closed without merging is not
    /// handled here: its card stays where it is (doc 12, <c>kanban.pr_closed_unmerged</c> = hold_flagged).
    /// </summary>
    public async Task<IReadOnlyList<PrMergeOutcome>> PrMergedAsync(string prRef, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prRef);
        var outcomes = new List<PrMergeOutcome>();
        foreach (var taskId in await _links.FindTaskIdsAsync(TaskLinkKind.Pr, prRef, ct).ConfigureAwait(false))
        {
            var current = await _tasks.GetAsync(taskId, ct).ConfigureAwait(false);
            if (current is null)
            {
                continue;
            }

            var result = await TransitionCoreAsync(taskId, TaskState.Done, TaskActor.SystemActor, $"pull request merged: {prRef}",
                current.Version, prMerged: true, ct).ConfigureAwait(false);
            outcomes.Add(new PrMergeOutcome(taskId, result));
        }

        return outcomes;
    }

    /// <summary>
    /// Creates a Backlog card from a message (doc 12, WP6, "pin"). The card's origin GC is the message's GC, and the message
    /// is kept twice: as <c>origin_message_id</c> (the database checks it against the GC) and as a <c>gc</c> task link.
    /// <paramref name="gcId"/> is the GC the pin is made from; a message in any other GC, or an unknown message, is refused
    /// and creates nothing.
    /// </summary>
    public async Task<CreateFromMessageResult> CreateFromMessageAsync(
        Guid projectId, Guid gcId, Guid messageId, string title, TaskActor actor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var messageGcs = await _tasks.FindMessageGcIdsAsync(messageId, ct).ConfigureAwait(false);
        if (messageGcs.Count == 0)
        {
            return CreateFromMessageResult.Refused(CreateFromMessageStatus.UnknownMessage);
        }

        if (!messageGcs.Contains(gcId))
        {
            return CreateFromMessageResult.Refused(CreateFromMessageStatus.OtherGc);
        }

        var taskId = Guid.CreateVersion7();
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await _tasks.InsertAsync(tx, new NewTask(taskId, projectId, title, TaskState.Backlog, null, TaskGate.None, gcId, messageId, null), ct)
            .ConfigureAwait(false);
        await _links.InsertAsync(tx, new TaskLink(taskId, TaskLinkKind.Gc, messageId.ToString("D")), ct).ConfigureAwait(false);
        var seq = await _events.AppendAsync(tx, new NewTaskEvent(taskId, TaskEventKinds.Created, actor.ToText(),
            Payload(("to", KanbanText.ToSql(TaskState.Backlog)), ("title", title), ("message_id", messageId.ToString("D")))), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        await PostReasonLinesAsync([new ReasonLine(taskId, gcId, seq, title, null, KanbanText.ToSql(TaskState.Backlog), "pinned from message")], actor, ct)
            .ConfigureAwait(false);
        return CreateFromMessageResult.Created(taskId);
    }

    /// <summary>
    /// Adds a link to a task (doc 12, Links). The reference is checked for shape first, then a GC link is checked for an
    /// existing GC, then the task row is locked and the link inserted. A refused reference writes nothing.
    /// <para>
    /// No <c>task.link_added</c> event is written: the <c>task_events</c> CHECK constraint (migration 0001_kanban_gates) does
    /// not allow that kind, and adding it needs a migration. Until then the <paramref name="actor"/> is not recorded.
    /// </para>
    /// </summary>
    public async Task<AddLinkResult> AddLinkAsync(Guid taskId, TaskLinkKind kind, string reference, TaskActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _ = actor;
        var problem = TaskLinkReferences.Check(kind, reference);
        if (problem is not null)
        {
            return AddLinkResult.Refuse(new LinkRefusal(kind, LinkRefusalReason.Malformed, problem));
        }

        if (kind == TaskLinkKind.Gc && !await _gcExistence.ExistsAsync(Guid.ParseExact(reference, "D"), ct).ConfigureAwait(false))
        {
            return AddLinkResult.Refuse(new LinkRefusal(kind, LinkRefusalReason.UnknownGc, "No GC has this id."));
        }

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        // Locking the task row serialises the link with transitions that read the links (the In progress to Review rule).
        var task = await _tasks.GetForUpdateAsync(tx, taskId, ct).ConfigureAwait(false);
        if (task is null)
        {
            return new AddLinkResult(LinkStatus.UnknownTask, null);
        }

        var inserted = await _links.InsertAsync(tx, new TaskLink(taskId, kind, reference), ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new AddLinkResult(inserted ? LinkStatus.Added : LinkStatus.AlreadyPresent, null);
    }

    /// <summary>
    /// Read-only start check for the dispatch path (doc 12, Interception). The coding harness calls it before it creates a
    /// worktree. It takes no lock and writes nothing; <see cref="TransitionAsync"/> repeats the check under lock.
    /// </summary>
    public async Task<StartCheck> CheckStartAsync(Guid taskId, CancellationToken ct = default)
    {
        var incoming = await _edges.ListBlockersAsync(taskId, null, ct).ConfigureAwait(false);
        var states = new Dictionary<Guid, TaskState>();
        foreach (var blockerId in incoming.Select(e => e.BlockerId).Distinct())
        {
            if (await _tasks.GetAsync(blockerId, ct).ConfigureAwait(false) is { } row)
            {
                states[blockerId] = row.State;
            }
        }

        return BlockedStatus.CheckStart(taskId, incoming, states);
    }

    private static async Task LockGraphAsync(NpgsqlTransaction tx, Guid projectId, bool exclusive, CancellationToken ct)
    {
        var function = exclusive ? "pg_advisory_xact_lock" : "pg_advisory_xact_lock_shared";
        await using var cmd = new NpgsqlCommand($"SELECT {function}(hashtextextended(@key, 0))", tx.Connection, tx);
        cmd.Parameters.AddWithValue("key", "kanban.graph:" + projectId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Locks the given rows in ascending id order and returns them. Missing ids are left out.</summary>
    private async Task<Dictionary<Guid, TaskRecord>> LockRowsAsync(NpgsqlTransaction tx, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var locked = new Dictionary<Guid, TaskRecord>();
        foreach (var id in ids.Distinct().Order())
        {
            if (await _tasks.GetForUpdateAsync(tx, id, ct).ConfigureAwait(false) is { } row)
            {
                locked[id] = row;
            }
        }

        return locked;
    }

    private async Task<Dictionary<Guid, TaskState>> StatesAsync(NpgsqlTransaction tx, Guid projectId, CancellationToken ct) =>
        (await _tasks.ListByProjectAsync(projectId, null, tx, ct).ConfigureAwait(false)).ToDictionary(t => t.Id, t => t.State);

    /// <summary>Writes task.blocked or task.unblocked for each dependent whose blocked status changed.</summary>
    private async Task EmitBlockedChangesAsync(
        NpgsqlTransaction tx,
        IEnumerable<Guid> dependents,
        IReadOnlyList<TaskEdge> graphBefore,
        IReadOnlyList<TaskEdge> graphAfter,
        IReadOnlyDictionary<Guid, TaskState> statesBefore,
        IReadOnlyDictionary<Guid, TaskState> statesAfter,
        TaskActor actor,
        CancellationToken ct)
    {
        foreach (var dependent in dependents.Distinct().Order())
        {
            var wasBlocked = BlockedStatus.IsBlocked(dependent, graphBefore, statesBefore);
            var isBlocked = BlockedStatus.IsBlocked(dependent, graphAfter, statesAfter);
            if (wasBlocked == isBlocked)
            {
                continue;
            }

            await _events.AppendAsync(tx, new NewTaskEvent(
                dependent,
                isBlocked ? TaskEventKinds.Blocked : TaskEventKinds.Unblocked,
                actor.ToText(),
                "{}"), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Posts one reason line per committed task event into the task's GC (doc 12, WP6). The line is a <c>system</c> event
    /// whose client message id is <c>kanban:&lt;taskId&gt;:&lt;eventSeq&gt;</c>: the event seq is unique and stable, so a repeat
    /// of the same line is answered with the original and never duplicated.
    /// <para>
    /// The GC log has its own transaction, so the line cannot join the task's transaction. It is posted after commit: the
    /// board change is never rolled back by a GC failure, and a failed post surfaces as an exception to the caller, who
    /// can retry the same change knowing the board already has it. An archived GC takes no new events (doc 01), so its
    /// lines are skipped and the change stands.
    /// </para>
    /// </summary>
    private async Task PostReasonLinesAsync(IReadOnlyList<ReasonLine> lines, TaskActor actor, CancellationToken ct)
    {
        if (_gcLog is null)
        {
            return;
        }

        foreach (var line in lines)
        {
            var request = new AppendRequest(line.GcId, GcEventKinds.System, AuthorKind.System, TaskActor.SystemActor.Id,
                SourceTrust.System, $"kanban:{line.TaskId:D}:{line.EventSeq}")
            {
                PayloadJson = Payload(
                    ("kind", "kanban.reason"),
                    ("task_id", line.TaskId.ToString("D")),
                    ("task_event_seq", line.EventSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("title", line.Title),
                    ("from", line.From),
                    ("to", line.To),
                    ("reason", line.Reason),
                    ("actor", actor.ToText())),
            };
            try
            {
                await _gcLog.AppendAsync(request, ct).ConfigureAwait(false);
            }
            catch (GcArchivedException)
            {
                // Archived GCs take no new events; the board change stands without its line.
            }
        }
    }

    /// <summary>A committed task event that gets a GC line. <see cref="From"/> is null on creation.</summary>
    private sealed record ReasonLine(
        Guid TaskId, Guid GcId, long EventSeq, string Title, string? From, string To, string? Reason);

    private static string Payload(params (string Key, string? Value)[] fields)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in fields)
        {
            obj[key] = value is null ? null : JsonValue.Create(value);
        }

        return obj.ToJsonString();
    }
}
