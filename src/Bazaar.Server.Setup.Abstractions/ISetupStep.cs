namespace Bazaar.Server.Setup;

/// <summary>
/// One step of the apply (doc 45 section 1.3). Steps run in ascending <see cref="Order"/>, ties broken by <see cref="Id"/>.
/// </summary>
/// <remarks>
/// Idempotency contract: <see cref="ApplyAsync"/> must be safe to run again. Running it on a state the step has already produced
/// changes nothing and reports success with the same result. A crash mid-apply therefore resumes from the last committed step.
/// A step writes its own transaction and must never write a secret value into any output, error text or result.
/// </remarks>
public interface ISetupStep
{
    /// <summary>Stable id, written to the audit row (for example <c>database.schema</c>).</summary>
    string Id { get; }

    /// <summary>Position in the apply order (doc 45 section 1.3). Lower runs first.</summary>
    int Order { get; }

    /// <summary>Applies the step. Idempotent: see the type remarks.</summary>
    Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Undoes what <see cref="ApplyAsync"/> did, when a later step fails and the apply rolls back. The default does nothing,
    /// for steps with nothing to undo. Rollback runs with a token that is not cancelled, so a rollback always finishes.
    /// </summary>
    Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// What a step reports. <paramref name="Message"/> is for the caller and never reaches the audit log. <see cref="AuditCounts"/> is
/// the only part of a result the applier writes to the audit row: counts only, never text (doc 22 Built details, PROPOSED).
/// </summary>
public sealed record SetupStepResult(bool Succeeded, string? Message = null)
{
    private static readonly IReadOnlyDictionary<string, int> _noCounts = new Dictionary<string, int>(0);

    private readonly IReadOnlyDictionary<string, int> _auditCounts = _noCounts;

    /// <summary>A step that succeeded.</summary>
    public static SetupStepResult Ok { get; } = new(true);

    /// <summary>
    /// Named counts for this step's audit row. The applier keeps only names matching <c>[a-z0-9._-]{1,64}</c> with a non-negative
    /// value, at most 32 of them, and drops the rest without failing the step. Empty by default, which leaves the audit row as it was.
    /// </summary>
    public IReadOnlyDictionary<string, int> AuditCounts
    {
        get => _auditCounts;
        init => _auditCounts = value ?? _noCounts;
    }

    /// <inheritdoc />
    public bool Equals(SetupStepResult? other) =>
        other is not null
        && Succeeded == other.Succeeded
        && string.Equals(Message, other.Message, StringComparison.Ordinal)
        && _auditCounts.Count == other._auditCounts.Count
        && _auditCounts.All(pair => other._auditCounts.TryGetValue(pair.Key, out var value) && value == pair.Value);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Succeeded, Message, _auditCounts.Count);
}

/// <summary>What a step gets besides the plan: the hash, the entry path, the operator, the template and the dry-run changes.</summary>
public sealed record SetupApplyContext(
    string PlanHash,
    SetupEntryPath EntryPath,
    string OperatorIdentity,
    string? TemplateId,
    string? TemplateVersion,
    SetupStateSnapshot CurrentState,
    IReadOnlyList<SetupChange> Changes);
