using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Usage;

/// <summary>
/// Collects one model call's events and builds its <c>gw_usage</c> row. It counts characters and keeps no text:
/// prompt and completion content never reach a usage row. Feed every <see cref="ModelEvent"/> through
/// <see cref="Observe"/>, then call <see cref="Build"/> once the stream has ended, failed or been cancelled.
/// </summary>
public sealed class UsageAccumulator
{
    private readonly string _agentId;
    private readonly string? _turnId;
    private readonly string _providerId;
    private readonly string _keyRef;
    private readonly string _modelId;
    private readonly GatewayIdentity _identity;
    private readonly TimeProvider _time;
    private readonly long _estimatedInputTokens;
    private readonly DateTimeOffset _startedAt;
    private readonly long _startTimestamp;

    private long _textChars;
    private long _thinkingChars;
    private long _argumentChars;
    private GatewayTokenUsage? _usage;
    private ModelEvent.Failure? _failure;
    private bool _done;
    private string? _servedModel;
    private int _recorded;

    /// <param name="agentId">The agent that made the call.</param>
    /// <param name="turnId">The turn the call belongs to, when there is one.</param>
    /// <param name="providerId">The provider the call went to.</param>
    /// <param name="keyRef">The secret reference of the key used. Never the key itself.</param>
    /// <param name="modelId">The model the caller requested.</param>
    /// <param name="identity">Whose key paid for the call: <c>native</c> or <c>pooled</c>.</param>
    /// <param name="time">The clock for the start time and the latency.</param>
    /// <param name="estimatedInputTokens">The input size to use when no usage was reported.</param>
    public UsageAccumulator(
        string agentId,
        string? turnId,
        string providerId,
        string keyRef,
        string modelId,
        GatewayIdentity identity,
        TimeProvider time,
        long estimatedInputTokens = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedInputTokens);

        _agentId = agentId;
        _turnId = turnId;
        _providerId = providerId;
        _keyRef = keyRef;
        _modelId = modelId;
        _identity = identity;
        _time = time;
        _estimatedInputTokens = estimatedInputTokens;
        _startedAt = time.GetUtcNow();
        _startTimestamp = time.GetTimestamp();
    }

    /// <summary>Counts one event. Text is measured and dropped; a later usage report replaces an earlier one.</summary>
    public void Observe(ModelEvent modelEvent)
    {
        ArgumentNullException.ThrowIfNull(modelEvent);
        switch (modelEvent)
        {
            case ModelEvent.TextDelta text:
                _textChars += text.Text.Length;
                break;

            case ModelEvent.ThinkingDelta thinking:
                _thinkingChars += thinking.Text.Length;
                break;

            case ModelEvent.ToolCallArgsDelta args:
                _argumentChars += args.ArgumentsFragment.Length;
                break;

            case ModelEvent.UsageReported usage:
                _usage = usage.Usage;
                break;

            case ModelEvent.Failure failure:
                _failure ??= failure;
                break;

            case ModelEvent.Done done:
                _done = true;
                _servedModel = done.ServedModel;
                break;

            default:
                // Tool call starts and ends, and warnings, carry no counted tokens.
                break;
        }
    }

    /// <summary>
    /// Builds the row. A failure makes it <c>Error</c>, a <c>Done</c> without a failure makes it <c>Success</c>, and
    /// anything else (a cut or cancelled stream) makes it <c>Aborted</c>. Without reported usage, or when aborted,
    /// the tokens are estimated from character counts and the row is marked estimated.
    /// </summary>
    public GatewayUsageRecord Build(PriceTable? prices)
    {
        var status = _failure is not null ? UsageStatus.Error
            : _done ? UsageStatus.Success
            : UsageStatus.Aborted;

        var estimated = _usage is null || status == UsageStatus.Aborted;
        var tokens = _usage ?? EstimateTokens();

        var priced = prices is not null && prices.Lookup(_providerId, _modelId, _startedAt) is not null;
        var latencyMs = (long)_time.GetElapsedTime(_startTimestamp).TotalMilliseconds;

        return new GatewayUsageRecord(
            Id: Guid.CreateVersion7(_startedAt),
            Timestamp: _startedAt,
            AgentId: _agentId,
            TurnId: _turnId,
            ProviderId: _providerId,
            KeyRef: _keyRef,
            ModelId: _modelId,
            ServedModel: _servedModel,
            Identity: _identity,
            Tokens: tokens,
            LatencyMs: latencyMs,
            Status: status,
            ErrorCode: _failure?.Code,
            RetryAfterSeconds: _failure?.RetryAfterSeconds,
            Estimated: estimated,
            PriceVersion: priced ? prices!.Version : null);
    }

    /// <summary>Claims the right to record this accumulator. Returns false if it was already claimed.</summary>
    internal bool TryClaimRecording() => Interlocked.CompareExchange(ref _recorded, 1, 0) == 0;

    /// <summary>Gives the claim back after a failed append, so the row can be recorded again.</summary>
    internal void ReleaseRecording() => Interlocked.Exchange(ref _recorded, 0);

    private GatewayTokenUsage EstimateTokens() => new(
        _estimatedInputTokens,
        0,
        0,
        0,
        CeilQuarter(_textChars + _thinkingChars + _argumentChars),
        CeilQuarter(_thinkingChars));

    /// <summary>Roughly four characters per token, rounded up.</summary>
    private static long CeilQuarter(long chars) => (chars + 3) / 4;
}
