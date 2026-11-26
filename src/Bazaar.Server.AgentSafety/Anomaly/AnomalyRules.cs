using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.AgentSafety.Anomaly;

/// <summary>Fires when one agent reads at least the threshold of objects of one class within the window (rule id <c>read_burst</c>).</summary>
public sealed class ReadBurstRule : IAnomalyRule
{
    /// <summary>The rule id.</summary>
    public const string RuleId = "read_burst";

    private readonly WindowedRuleOptions _options;
    private readonly SlidingWindowCounter<(string AgentId, string ObjectClass)> _counter;

    public ReadBurstRule(WindowedRuleOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(nameof(ReadBurstRule));
        _options = options;
        _counter = new(options.Threshold, options.Window, SlidingWindowCounter<(string, string)>.DefaultMaxKeys, key => key.Item1, time);
    }

    public string Id => RuleId;

    public IReadOnlyList<AnomalyAlert> Evaluate(AnomalyEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind != AnomalyEventKind.ObjectRead)
        {
            return [];
        }

        var objectClass = e.ObjectClass ?? string.Empty;
        var agent = AgentIds.Canonical(e.AgentId ?? string.Empty);
        if (!_counter.Observe((agent, objectClass), e.At))
        {
            return [];
        }

        var detail = $"{_options.Threshold} reads of '{e.ObjectClass ?? "unclassified"}' objects within {_options.Window}.";
        return [new AnomalyAlert(RuleId, agent, e.SessionId, _options.Severity, e.At, detail)];
    }
}

/// <summary>Fires on every scoped access that falls outside the agent's scope (rule id <c>out_of_scope_read</c>).</summary>
public sealed class OutOfScopeReadRule(SeverityRuleOptions options) : IAnomalyRule
{
    /// <summary>The rule id.</summary>
    public const string RuleId = "out_of_scope_read";

    private readonly SeverityRuleOptions _options = Validated(options);

    public string Id => RuleId;

    public IReadOnlyList<AnomalyAlert> Evaluate(AnomalyEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind != AnomalyEventKind.ScopedAccess || e.InScope)
        {
            return [];
        }

        var detail = $"Read outside the agent's scope ({e.ObjectClass ?? "unclassified"}).";
        return [new AnomalyAlert(RuleId, AgentIds.Canonical(e.AgentId ?? string.Empty), e.SessionId, _options.Severity, e.At, detail)];
    }

    private static SeverityRuleOptions Validated(SeverityRuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(nameof(OutOfScopeReadRule));
        return options;
    }
}

/// <summary>Fires on every canary touch (rule id <c>canary_hit</c>). A canary has no legitimate use, so each touch is one alert.</summary>
public sealed class CanaryHitRule(SeverityRuleOptions options) : IAnomalyRule
{
    /// <summary>The rule id.</summary>
    public const string RuleId = "canary_hit";

    private readonly SeverityRuleOptions _options = Validated(options);

    public string Id => RuleId;

    public IReadOnlyList<AnomalyAlert> Evaluate(AnomalyEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind != AnomalyEventKind.CanaryTouched)
        {
            return [];
        }

        var detail = $"A canary was touched ({e.ObjectClass ?? "unclassified"}).";
        return [new AnomalyAlert(RuleId, AgentIds.Canonical(e.AgentId ?? string.Empty), e.SessionId, _options.Severity, e.At, detail)];
    }

    private static SeverityRuleOptions Validated(SeverityRuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(nameof(CanaryHitRule));
        return options;
    }
}

/// <summary>Fires when one agent asks for at least the threshold of approvals within the window (rule id <c>approval_flood</c>).</summary>
public sealed class ApprovalFloodRule : IAnomalyRule
{
    /// <summary>The rule id.</summary>
    public const string RuleId = "approval_flood";

    private readonly WindowedRuleOptions _options;
    private readonly SlidingWindowCounter<string> _counter;

    public ApprovalFloodRule(WindowedRuleOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(nameof(ApprovalFloodRule));
        _options = options;
        _counter = new(options.Threshold, options.Window, SlidingWindowCounter<string>.DefaultMaxKeys, agent => agent, time);
    }

    public string Id => RuleId;

    public IReadOnlyList<AnomalyAlert> Evaluate(AnomalyEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Kind != AnomalyEventKind.ApprovalRequested)
        {
            return [];
        }

        var agent = AgentIds.Canonical(e.AgentId ?? string.Empty);
        if (!_counter.Observe(agent, e.At))
        {
            return [];
        }

        var detail = $"{_options.Threshold} approval requests within {_options.Window}.";
        return [new AnomalyAlert(RuleId, agent, e.SessionId, _options.Severity, e.At, detail)];
    }
}
