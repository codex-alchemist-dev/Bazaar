using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.AgentSafety.AutoContainment;

/// <summary>Outcome of <see cref="IAutoContainmentUndo.UndoAsync"/>.</summary>
public enum AutoContainmentUndoOutcome
{
    /// <summary>The signed release succeeded inside the window; the record is Undone.</summary>
    Undone,

    /// <summary>The agent has no automatic containment in force (none recorded, already undone, failed, or released).</summary>
    NotInForce,

    /// <summary>The undo window had elapsed. Nothing was released; only the normal owner release path applies.</summary>
    WindowExpired,

    /// <summary>The signed release was not accepted (<see cref="ContainmentReleaseOutcome.Denied"/> or not contained). Nothing was undone.</summary>
    ReleaseRefused,
}

/// <summary>Result of an undo request.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Release">The release result, when a release was attempted.</param>
public sealed record AutoContainmentUndoResult(AutoContainmentUndoOutcome Outcome, ContainmentReleaseResult? Release = null);

/// <summary>Undo of an automatic containment (doc 48 section 5.6): the owner's signed release inside the undo window.</summary>
public interface IAutoContainmentUndo
{
    /// <summary>
    /// Undoes the agent's automatic containment. The undo happens only when <see cref="IAgentContainment.ReleaseAsync"/> returns
    /// <see cref="ContainmentReleaseOutcome.Released"/> for the signed <paramref name="request"/> and the window is still open.
    /// </summary>
    Task<AutoContainmentUndoResult> UndoAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IAutoContainmentUndo"/>. It adds no authority of its own: the owner's device signature, the single-use
/// nonce and the device registry are checked by <see cref="IAgentContainment.ReleaseAsync"/>, which this class calls and never
/// bypasses. It refuses without calling release when the window has elapsed, marks the record Undone only after a Released
/// outcome, and audits every refusal and the undo.
/// </summary>
public sealed partial class AutoContainmentUndo : IAutoContainmentUndo
{
    private const string _undoActor = "anomaly:undo";

    private readonly IAgentContainment _containment;
    private readonly IContainmentRecordStore _records;
    private readonly IAuditLog _audit;
    private readonly INoticeSink _notices;
    private readonly TimeProvider _time;
    private readonly ILogger<AutoContainmentUndo> _logger;

    /// <summary>Creates the undo service.</summary>
    public AutoContainmentUndo(
        IAgentContainment containment,
        IContainmentRecordStore records,
        IAuditLog audit,
        INoticeSink notices,
        TimeProvider time,
        ILogger<AutoContainmentUndo> logger)
    {
        _containment = containment ?? throw new ArgumentNullException(nameof(containment));
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _notices = notices ?? throw new ArgumentNullException(nameof(notices));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AutoContainmentUndoResult> UndoAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(request);

        var record = await _records.GetLatestAsync(agentId, ct).ConfigureAwait(false);
        if (record is null || record.State != ContainmentRecordState.InForce)
        {
            await AuditAsync("undo_refused_not_in_force", agentId, record, ct).ConfigureAwait(false);
            return new AutoContainmentUndoResult(AutoContainmentUndoOutcome.NotInForce);
        }

        if (_time.GetUtcNow() > record.UndoUntil)
        {
            await _records.TryTransitionAsync(record.Id, ContainmentRecordState.InForce, ContainmentRecordState.WindowExpired, ct).ConfigureAwait(false);
            await AuditAsync("undo_refused_window_expired", agentId, record, ct).ConfigureAwait(false);
            return new AutoContainmentUndoResult(AutoContainmentUndoOutcome.WindowExpired);
        }

        // The owner's signature, nonce and device are verified here; this class never decides who may release.
        var release = await _containment.ReleaseAsync(agentId, request, ct).ConfigureAwait(false);
        if (release.Outcome != ContainmentReleaseOutcome.Released)
        {
            await AuditAsync("undo_refused_release_" + release.Outcome.ToString().ToLowerInvariant(), agentId, record, ct).ConfigureAwait(false);
            return new AutoContainmentUndoResult(AutoContainmentUndoOutcome.ReleaseRefused, release);
        }

        var moved = await _records.TryTransitionAsync(record.Id, ContainmentRecordState.InForce, ContainmentRecordState.Undone, ct).ConfigureAwait(false);
        if (!moved)
        {
            LogRecordNotMoved();
        }
        await AuditAsync("undone", agentId, record, ct).ConfigureAwait(false);

        try
        {
            await _notices.PublishAsync(
                new ContainmentNotice(ContainmentNoticeKind.Undone, agentId, record.RuleId, record.Severity, null, "Containment undone by the owner."), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNoticeFailed(ex);
        }

        return new AutoContainmentUndoResult(AutoContainmentUndoOutcome.Undone, release);
    }

    private async Task AuditAsync(string step, string agentId, ContainmentRecord? record, CancellationToken ct)
    {
        try
        {
            await AutoContainmentAudit.AppendAsync(
                _audit, _time, _undoActor, step, agentId, record?.RuleId, record?.Severity.ToString(), record?.UndoUntil, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAuditFailed(ex, step);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The agent was released but its containment record had already changed state.")]
    private partial void LogRecordNotMoved();

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the {Step} audit record failed.")]
    private partial void LogAuditFailed(Exception ex, string step);

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing the undo notice failed; the undo stands.")]
    private partial void LogNoticeFailed(Exception ex);
}
