namespace Bazaar.Kodo.Anchoring;

/// <summary>What happened to one sink during a publish.</summary>
public enum AnchorSinkOutcome
{
    /// <summary>The sink accepted the anchor.</summary>
    Accepted,

    /// <summary>The sink declined the anchor.</summary>
    Rejected,

    /// <summary>The sink threw.</summary>
    Failed,

    /// <summary>The sink did not answer within the timeout.</summary>
    TimedOut,
}

/// <summary>Result for one sink.</summary>
public sealed record AnchorSinkResult(string SinkId, AnchorSinkOutcome Outcome);

/// <summary>Results for every sink. With no sinks the journal is simply not anchored, and <see cref="AnyAccepted"/> is false.</summary>
public sealed record AnchorPublishReport(IReadOnlyList<AnchorSinkResult> Results)
{
    /// <summary>True when at least one sink accepted. Only then may the planner treat the anchor as done.</summary>
    public bool AnyAccepted => Results.Any(r => r.Outcome == AnchorSinkOutcome.Accepted);
}

/// <summary>
/// Publishes an anchor to every sink and contains every failure: a sink that throws, hangs or declines never
/// stops the journal or the other sinks (doc 46 section 6.5.1). Each sink gets its own timeout. Only the caller's
/// own cancellation propagates.
/// </summary>
public sealed class AnchorPublisher
{
    /// <summary>Default per-sink timeout.</summary>
    public static readonly TimeSpan DefaultSinkTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _sinkTimeout;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a publisher. <paramref name="sinkTimeout"/> must be positive.</summary>
    public AnchorPublisher(TimeSpan? sinkTimeout = null, TimeProvider? timeProvider = null)
    {
        _sinkTimeout = sinkTimeout ?? DefaultSinkTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_sinkTimeout, TimeSpan.Zero);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Publishes to all sinks, one after another. Never throws for a sink problem.</summary>
    public async Task<AnchorPublishReport> PublishAsync(AnchorRecord anchor, IEnumerable<IAnchorSink> sinks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(sinks);

        List<AnchorSinkResult> results = [];
        foreach (IAnchorSink sink in sinks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(new AnchorSinkResult(SafeId(sink), await PublishOneAsync(anchor, sink, cancellationToken)));
        }

        return new AnchorPublishReport(results);
    }

    private static string SafeId(IAnchorSink sink)
    {
        try
        {
            return sink.Id ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private async Task<AnchorSinkOutcome> PublishOneAsync(AnchorRecord anchor, IAnchorSink sink, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(_sinkTimeout, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            bool accepted = await sink.PublishAsync(anchor, linked.Token);
            return accepted ? AnchorSinkOutcome.Accepted : AnchorSinkOutcome.Rejected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return AnchorSinkOutcome.TimedOut;
        }
        catch (Exception)
        {
            return AnchorSinkOutcome.Failed;
        }
    }
}
