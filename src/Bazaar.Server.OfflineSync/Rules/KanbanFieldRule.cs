using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>The answer of the transition rules for a status change.</summary>
/// <param name="Allowed">Whether the transition may apply.</param>
/// <param name="Reason">Why it is not allowed, when <paramref name="Allowed"/> is false.</param>
public sealed record TransitionDecision(bool Allowed, string? Reason = null);

/// <summary>Checks Kanban status transitions after a merge (doc 56 section 5, item 3; doc 12).</summary>
public interface ITransitionChecker
{
    /// <summary>Decides whether a task may move between two statuses.</summary>
    /// <param name="taskId">The task.</param>
    /// <param name="fromStatus">The current status, or null when none is set.</param>
    /// <param name="toStatus">The requested status.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask<TransitionDecision> CheckAsync(string taskId, string? fromStatus, string toStatus, CancellationToken cancellationToken);
}

/// <summary>A stored Kanban field: its value and its version. An absent field has a null value and version 0.</summary>
/// <param name="Value">The field value, or null when the field was never set.</param>
/// <param name="Version">The field version.</param>
public readonly record struct KanbanFieldSnapshot(string? Value, long Version);

/// <summary>State the Kanban field rule needs: write-id claims and versioned field storage.</summary>
public interface IKanbanFieldState
{
    /// <summary>Atomically claims a write id. Returns false when the id was already claimed.</summary>
    ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken);

    /// <summary>Reads a field. A never-set field reads as a null value at version 0.</summary>
    ValueTask<KanbanFieldSnapshot> GetFieldAsync(string taskId, string field, CancellationToken cancellationToken);

    /// <summary>
    /// Compare-and-set: stores <paramref name="value"/> at version <paramref name="expectedVersion"/> + 1
    /// only when the stored version still equals <paramref name="expectedVersion"/>.
    /// </summary>
    ValueTask<bool> TryReplaceFieldAsync(string taskId, string field, long expectedVersion, string value, CancellationToken cancellationToken);
}

/// <summary>
/// Merge rule for Kanban field writes (doc 56 section 5, item 3). A write applies only when the server
/// field version equals its base version. Otherwise it is a conflict and opens a review item. Status
/// writes also pass through <see cref="ITransitionChecker"/>. Blocked is never merged; the checker
/// decides it after the merge.
/// </summary>
public sealed class KanbanFieldRule : IOfflineMergeRule
{
    /// <summary>The field name that goes through transition rules.</summary>
    public const string StatusField = "status";

    private readonly IKanbanFieldState _state;
    private readonly ITransitionChecker _transitions;
    private readonly IReviewSink _review;

    /// <summary>Creates the rule.</summary>
    /// <param name="state">The field state.</param>
    /// <param name="transitions">Checks status transitions.</param>
    /// <param name="review">Receives conflict review items.</param>
    public KanbanFieldRule(IKanbanFieldState state, ITransitionChecker transitions, IReviewSink review)
    {
        _state = state;
        _transitions = transitions;
        _review = review;
    }

    /// <inheritdoc />
    public OfflineDataClass DataClass => OfflineDataClass.KanbanField;

    /// <inheritdoc />
    public async ValueTask<OfflineWriteOutcome> MergeAsync(OfflineWrite write, CancellationToken cancellationToken)
    {
        var payload = RulePayloadCodec.ReadKanbanField(write.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Field))
        {
            return RuleOutcomes.Rejected(write.WriteId, "Payload is not a Kanban field write.");
        }

        if (!await _state.TryClaimWriteAsync(write.WriteId, cancellationToken))
        {
            return RuleOutcomes.Duplicate(write.WriteId);
        }

        var baseVersion = RuleVersions.Parse(write.BaseVersion);
        if (baseVersion is null)
        {
            return RuleOutcomes.Rejected(write.WriteId, "Field writes must name a numeric base version.");
        }

        var current = await _state.GetFieldAsync(write.Target, payload.Field, cancellationToken);
        if (current.Version != baseVersion.Value)
        {
            return await ConflictAsync(write, RuleVersions.Format(current.Version), "Field changed since the base version.", cancellationToken);
        }

        if (payload.Field == StatusField)
        {
            var decision = await _transitions.CheckAsync(write.Target, current.Value, payload.Value, cancellationToken);
            if (!decision.Allowed)
            {
                return RuleOutcomes.Rejected(write.WriteId, decision.Reason ?? "Status transition is not allowed.");
            }
        }

        if (!await _state.TryReplaceFieldAsync(write.Target, payload.Field, current.Version, payload.Value, cancellationToken))
        {
            var latest = await _state.GetFieldAsync(write.Target, payload.Field, cancellationToken);
            return await ConflictAsync(write, RuleVersions.Format(latest.Version), "Field changed during the merge.", cancellationToken);
        }

        return RuleOutcomes.Applied(write.WriteId, RuleVersions.Format(current.Version + 1));
    }

    private async ValueTask<OfflineWriteOutcome> ConflictAsync(OfflineWrite write, string serverVersion, string reason, CancellationToken cancellationToken)
    {
        await _review.OpenAsync(new ReviewItem("kanban-field-conflict", write.Target, write.WriteId, write.BaseVersion, serverVersion, reason), cancellationToken);
        return new OfflineWriteOutcome(write.WriteId, OfflineOutcomeKind.Conflict, serverVersion, reason);
    }
}

/// <summary>In-memory <see cref="IKanbanFieldState"/> for tests and single-process use. Thread safe.</summary>
public sealed class InMemoryKanbanFieldState : IKanbanFieldState
{
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _claimed = [];
    private readonly Dictionary<(string TaskId, string Field), KanbanFieldSnapshot> _fields = [];

    /// <inheritdoc />
    public ValueTask<bool> TryClaimWriteAsync(Guid writeId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_claimed.Add(writeId));
        }
    }

    /// <inheritdoc />
    public ValueTask<KanbanFieldSnapshot> GetFieldAsync(string taskId, string field, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_fields.TryGetValue((taskId, field), out var snapshot) ? snapshot : new KanbanFieldSnapshot(null, 0));
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TryReplaceFieldAsync(string taskId, string field, long expectedVersion, string value, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var current = _fields.TryGetValue((taskId, field), out var snapshot) ? snapshot : new KanbanFieldSnapshot(null, 0);
            if (current.Version != expectedVersion)
            {
                return ValueTask.FromResult(false);
            }

            _fields[(taskId, field)] = new KanbanFieldSnapshot(value, expectedVersion + 1);
            return ValueTask.FromResult(true);
        }
    }
}
