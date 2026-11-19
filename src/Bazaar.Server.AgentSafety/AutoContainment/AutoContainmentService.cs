using Bazaar.Server.AgentSafety.Anomaly;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AgentSafety.AutoContainment;

/// <summary>
/// Turns anomaly alerts into automatic containment of the one agent an alert is about (doc 48 section 5.6). An alert at or above
/// <see cref="AutoContainmentOptions.Threshold"/> contains that agent through <see cref="IAgentContainment.ContainAsync"/> with the
/// breach actor <c>anomaly:&lt;ruleId&gt;</c>; a lower alert is only logged. Global containment is never started here: it needs the
/// owner's role.
/// </summary>
/// <remarks>
/// Alerts are handled one at a time, so a burst for one agent yields one containment; an agent that is already contained (by the
/// store or by an InForce record) is not contained again. A record is written as <see cref="ContainmentRecordState.InForce"/> only after
/// <see cref="IAgentContainment.ContainAsync"/> completed. A throwing contain is retried with doubling backoff on the injected
/// <see cref="TimeProvider"/>; when attempts run out a <see cref="ContainmentRecordState.Failed"/> record and a notice say so. Notice
/// failures are logged and never block containment. A second loop marks elapsed undo windows as expired.
/// </remarks>
public sealed partial class AutoContainmentService : BackgroundService
{
    private readonly IAnomalyAlertSource _alerts;
    private readonly IAgentContainment _containment;
    private readonly IContainmentStateStore _containedStore;
    private readonly IContainmentRecordStore _records;
    private readonly IAuditLog _audit;
    private readonly INoticeSink _notices;
    private readonly AutoContainmentOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AutoContainmentService> _logger;

    /// <summary>Creates the service. Throws when the options are invalid.</summary>
    public AutoContainmentService(
        IAnomalyAlertSource alerts,
        IAgentContainment containment,
        IContainmentStateStore containedStore,
        IContainmentRecordStore records,
        IAuditLog audit,
        INoticeSink notices,
        IOptions<AutoContainmentOptions> options,
        TimeProvider time,
        ILogger<AutoContainmentService> logger)
    {
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
        _containment = containment ?? throw new ArgumentNullException(nameof(containment));
        _containedStore = containedStore ?? throw new ArgumentNullException(nameof(containedStore));
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _notices = notices ?? throw new ArgumentNullException(nameof(notices));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException("Options value is null.", nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var problem = _options.Validate();
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(options));
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ReadAlertsAsync(stoppingToken), SweepAsync(stoppingToken));

    private async Task ReadAlertsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var alert in _alerts.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleAlertAsync(alert, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogAlertFailed(ex, alert?.RuleId ?? "unknown");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.ExpirySweepInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _records.ExpireDueAsync(_time.GetUtcNow(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogSweepFailed(ex);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Applies the policy to one alert. Public so the policy can be driven directly.</summary>
    public async Task HandleAlertAsync(AnomalyAlert alert, CancellationToken ct)
    {
        if (alert is null || string.IsNullOrWhiteSpace(alert.AgentId) || string.IsNullOrWhiteSpace(alert.RuleId))
        {
            LogMalformedAlert();
            return;
        }

        if (alert.Severity < _options.Threshold)
        {
            LogBelowThreshold(alert.RuleId, alert.Severity);
            return;
        }

        var latest = await _records.GetLatestAsync(alert.AgentId, ct).ConfigureAwait(false);
        var contained = (await _containedStore.LoadAsync(ct).ConfigureAwait(false)).Any(a => a.AgentId == alert.AgentId);
        if (contained && latest?.State != ContainmentRecordState.Failed)
        {
            LogAlreadyContained(alert.RuleId);
            return;
        }

        if (latest?.State == ContainmentRecordState.InForce)
        {
            // The agent is not contained any more, so the owner released it by the normal path: the record is history.
            await _records.TryTransitionAsync(latest.Id, ContainmentRecordState.InForce, ContainmentRecordState.Released, ct).ConfigureAwait(false);
        }

        var actor = AutoContainmentAudit.BreachActor(alert.RuleId);
        var severity = alert.Severity.ToString();
        var failure = await ContainWithRetryAsync(alert, actor, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        if (failure is not null)
        {
            await _records.TryAddAsync(
                new ContainmentRecord(Guid.NewGuid(), alert.AgentId, alert.RuleId, alert.Severity, now, now, ContainmentRecordState.Failed), ct).ConfigureAwait(false);
            LogContainFailed(failure, alert.RuleId, _options.MaxContainAttempts);
            await SafeAuditAsync(actor, "contain_failed", alert, severity, null, ct).ConfigureAwait(false);
            await SafeNoticeAsync(new ContainmentNotice(ContainmentNoticeKind.ContainmentFailed, alert.AgentId, alert.RuleId, alert.Severity, null, alert.Detail), ct).ConfigureAwait(false);
            return;
        }

        var undoUntil = now + _options.UndoWindow;
        var added = await _records.TryAddAsync(
            new ContainmentRecord(Guid.NewGuid(), alert.AgentId, alert.RuleId, alert.Severity, now, undoUntil, ContainmentRecordState.InForce), ct).ConfigureAwait(false);
        if (!added)
        {
            LogAlreadyContained(alert.RuleId);
            return;
        }

        LogContained(alert.RuleId, severity);
        await SafeAuditAsync(actor, "contained", alert, severity, undoUntil, ct).ConfigureAwait(false);
        await SafeNoticeAsync(new ContainmentNotice(ContainmentNoticeKind.Contained, alert.AgentId, alert.RuleId, alert.Severity, undoUntil, alert.Detail), ct).ConfigureAwait(false);
    }

    /// <summary>Contains the agent, retrying a throwing contain. Returns the last exception when every attempt failed, otherwise null.</summary>
    private async Task<Exception?> ContainWithRetryAsync(AnomalyAlert alert, string actor, CancellationToken ct)
    {
        var delay = _options.RetryBackoff;
        Exception? last = null;
        for (var attempt = 1; attempt <= _options.MaxContainAttempts; attempt++)
        {
            try
            {
                await _containment.ContainAsync(alert.AgentId, actor, ct).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                LogContainAttemptFailed(ex, alert.RuleId, attempt);
            }

            if (attempt < _options.MaxContainAttempts)
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                delay += delay;
            }
        }
        return last;
    }

    private async Task SafeAuditAsync(string actor, string step, AnomalyAlert alert, string severity, DateTimeOffset? undoUntil, CancellationToken ct)
    {
        try
        {
            await AutoContainmentAudit.AppendAsync(_audit, _time, actor, step, alert.AgentId, alert.RuleId, severity, undoUntil, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAuditFailed(ex, step);
        }
    }

    private async Task SafeNoticeAsync(ContainmentNotice notice, CancellationToken ct)
    {
        try
        {
            await _notices.PublishAsync(notice, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogNoticeFailed(ex, notice.Kind);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored a malformed anomaly alert.")]
    private partial void LogMalformedAlert();

    [LoggerMessage(Level = LogLevel.Information, Message = "Anomaly alert from rule {RuleId} ({Severity}) is below the containment threshold; recorded only.")]
    private partial void LogBelowThreshold(string ruleId, AnomalySeverity severity);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anomaly alert from rule {RuleId}: the agent is already contained; not contained again.")]
    private partial void LogAlreadyContained(string ruleId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Automatic containment by rule {RuleId} ({Severity}) completed.")]
    private partial void LogContained(string ruleId, string severity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Containment attempt {Attempt} for an alert from rule {RuleId} failed.")]
    private partial void LogContainAttemptFailed(Exception ex, string ruleId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Automatic containment for an alert from rule {RuleId} failed after {Attempts} attempts.")]
    private partial void LogContainFailed(Exception ex, string ruleId, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling an anomaly alert from rule {RuleId} failed.")]
    private partial void LogAlertFailed(Exception ex, string ruleId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the {Step} audit record failed; containment is unaffected.")]
    private partial void LogAuditFailed(Exception ex, string step);

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing a {Kind} notice failed; containment is unaffected.")]
    private partial void LogNoticeFailed(Exception ex, ContainmentNoticeKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Marking elapsed undo windows failed.")]
    private partial void LogSweepFailed(Exception ex);
}
