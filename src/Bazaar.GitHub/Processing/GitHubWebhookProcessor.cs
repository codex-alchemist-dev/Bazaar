using Bazaar.GitHub.Events;
using Bazaar.GitHub.Ingress;
using Microsoft.Extensions.Logging;

namespace Bazaar.GitHub.Processing;

/// <summary>
/// Processes stored webhook deliveries (doc 21, Ingress). One <see cref="ProcessBatchAsync"/> claims due rows from the queue, parses each
/// with <see cref="GitHubWebhookPayloadParser"/>, applies <see cref="GitHubEventOrdering"/> against the last applied state of the PR (in
/// memory, per instance), maps with <see cref="GitHubEventMapper"/> and delivers to <see cref="IGitHubEventSink"/>.
/// <list type="bullet">
/// <item>The row is marked processed only after the sink accepted it.</item>
/// <item>A sink (or mapper) failure leaves it unprocessed; it is claimable again after an exponential backoff.</item>
/// <item>An unparseable row is a poison row: marked processed with the reason in <c>process_error</c>, so it never loops.</item>
/// <item>An event type the mapping does not use is marked processed without calling the sink.</item>
/// </list>
/// </summary>
public sealed class GitHubWebhookProcessor
{
    private static readonly Action<ILogger, string, string, Exception?> _logPoison = LoggerMessage.Define<string, string>(
        LogLevel.Warning, new EventId(30, "GitHubWebhookPoison"), "GitHub delivery {DeliveryId} cannot be processed and is marked done: {Reason}.");

    private static readonly Action<ILogger, string, int, Exception?> _logRetry = LoggerMessage.Define<string, int>(
        LogLevel.Warning, new EventId(31, "GitHubWebhookRetry"), "GitHub delivery {DeliveryId} failed on attempt {Attempts}; it will be retried after a backoff.");

    private readonly IGitHubDeliveryInbox _queue;
    private readonly IGitHubEventSink _sink;
    private readonly GitHubEventMapper _mapper;
    private readonly GitHubWebhookProcessorOptions _options;
    private readonly ILogger<GitHubWebhookProcessor> _logger;
    private readonly TimeProvider _time;
    private readonly IReadOnlySet<string> _memberLogins;
    private readonly Dictionary<string, (DateTimeOffset UpdatedAt, string? HeadSha)> _applied = new(StringComparer.Ordinal);

    public GitHubWebhookProcessor(
        IGitHubDeliveryInbox queue,
        IGitHubEventSink sink,
        GitHubIngressOptions ingress,
        GitHubWebhookProcessorOptions options,
        ILogger<GitHubWebhookProcessor> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(ingress);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _queue = queue;
        _sink = sink;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _mapper = new GitHubEventMapper(ingress.AgentLogins);
        _memberLogins = new HashSet<string>(ingress.MemberLogins.Concat(ingress.AgentLogins), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Claims and processes one batch. Returns how many rows were claimed (0 means idle). Never throws for a per-row failure.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var claimed = await _queue.ClaimAsync(Math.Max(1, _options.BatchSize), _time.GetUtcNow(), _options.ClaimLease, cancellationToken).ConfigureAwait(false);
        foreach (var row in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessOneAsync(row, cancellationToken).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    private async Task ProcessOneAsync(ClaimedDelivery row, CancellationToken ct)
    {
        var parsed = GitHubWebhookPayloadParser.Parse(row.EventType, row.DeliveryId, row.PayloadJson, row.ReceivedAt, _memberLogins);
        switch (parsed)
        {
            case WebhookParseResult.Ignored:
                await _queue.CompleteAsync(row.Source, row.DeliveryId, _time.GetUtcNow(), ct).ConfigureAwait(false);
                return;
            case WebhookParseResult.Malformed malformed:
                _logPoison(_logger, row.DeliveryId, malformed.Reason, null);
                await _queue.PoisonAsync(row.Source, row.DeliveryId, _time.GetUtcNow(), malformed.Reason, ct).ConfigureAwait(false);
                return;
        }

        var gitHubEvent = ((WebhookParseResult.Parsed)parsed).Event;
        try
        {
            var apply = true;
            var subject = gitHubEvent.PrNodeId;
            if (subject is not null && _applied.TryGetValue(subject, out var last))
            {
                apply = GitHubEventOrdering.Decide(gitHubEvent, last.UpdatedAt, last.HeadSha).Apply;
            }

            var effects = apply ? _mapper.Map(gitHubEvent) : [];
            await _sink.DeliverAsync(new GitHubEventDelivery(gitHubEvent, GitHubEventDedupe.KeyFor(gitHubEvent), apply, effects), ct).ConfigureAwait(false);
            if (apply && subject is not null)
            {
                Remember(subject, gitHubEvent);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logRetry(_logger, row.DeliveryId, row.Attempts, ex);
            await _queue.RetryLaterAsync(row.Source, row.DeliveryId, _time.GetUtcNow() + _options.BackoffFor(row.Attempts), ct).ConfigureAwait(false);
            return;
        }

        await _queue.CompleteAsync(row.Source, row.DeliveryId, _time.GetUtcNow(), ct).ConfigureAwait(false);
    }

    private void Remember(string prNodeId, GitHubEvent e)
    {
        if (!_applied.ContainsKey(prNodeId) && _applied.Count >= _options.MaxTrackedPullRequests)
        {
            _applied.Remove(_applied.Keys.First());
        }

        var previous = _applied.TryGetValue(prNodeId, out var last) ? last : default;
        _applied[prNodeId] = (e.UpdatedAt >= previous.UpdatedAt ? e.UpdatedAt : previous.UpdatedAt, e.HeadSha ?? previous.HeadSha);
    }
}
