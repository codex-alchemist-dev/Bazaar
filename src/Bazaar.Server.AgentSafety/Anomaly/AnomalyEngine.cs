using Microsoft.Extensions.Logging;

namespace Bazaar.Server.AgentSafety.Anomaly;

/// <summary>
/// Runs every anomaly rule on each event and publishes the alerts they raise (doc 48 section 5.6).
/// </summary>
/// <remarks>
/// A rule that throws, or a sink that throws for one alert, is isolated: the other rules and alerts still run. The failure goes to
/// <c>onRuleError</c> when one is given, and to the log otherwise, so it is never dropped silently.
/// </remarks>
public sealed partial class AnomalyEngine
{
    private readonly IReadOnlyList<IAnomalyRule> _rules;
    private readonly IAnomalyAlertSink _sink;
    private readonly ILogger<AnomalyEngine> _logger;
    private readonly Action<string, Exception>? _onRuleError;

    public AnomalyEngine(
        IEnumerable<IAnomalyRule> rules,
        IAnomalyAlertSink sink,
        ILogger<AnomalyEngine> logger,
        Action<string, Exception>? onRuleError = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = [.. rules];
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _onRuleError = onRuleError;
    }

    /// <summary>Feeds one event to every rule in turn and publishes each alert that comes out.</summary>
    public async ValueTask HandleAsync(AnomalyEvent e, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(e);
        foreach (var rule in _rules)
        {
            IReadOnlyList<AnomalyAlert> alerts;
            try
            {
                alerts = rule.Evaluate(e);
            }
            catch (Exception ex)
            {
                ReportFailure(rule.Id, ex);
                continue;
            }

            foreach (var alert in alerts)
            {
                await PublishAsync(rule.Id, alert, ct).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask PublishAsync(string ruleId, AnomalyAlert alert, CancellationToken ct)
    {
        try
        {
            await _sink.PublishAsync(alert, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportFailure(ruleId, ex);
        }
    }

    private void ReportFailure(string ruleId, Exception exception)
    {
        if (_onRuleError is not null)
        {
            _onRuleError(ruleId, exception);
            return;
        }

        LogRuleFailed(exception, ruleId);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Anomaly rule {RuleId} failed or its alert could not be published; the other rules still ran.")]
    private partial void LogRuleFailed(Exception exception, string ruleId);
}
