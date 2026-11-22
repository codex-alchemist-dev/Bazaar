using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Events;
using Microsoft.Extensions.Logging;

namespace Bazaar.GitHub.Ingress;

/// <summary>
/// The Events API poller (doc 21, Ingress). One <see cref="PollDueAsync"/> call polls every repository that is due, using
/// conditional requests, then parses, dedupes, orders and maps the events and hands them to <see cref="IGitHubEventSink"/>.
/// <list type="bullet">
/// <item>Cadence per repo: the larger of <c>github.poll_interval_minutes</c> and GitHub's <c>X-Poll-Interval</c>.</item>
/// <item>Rate limit exhausted: the repo waits until the reset time GitHub reported.</item>
/// <item>Any other failure: exponential backoff per repo; the exception never leaves this class except cancellation.</item>
/// <item>An event is marked processed only after the sink accepted it, so a sink failure redelivers it; the ETag only advances when
/// the whole page was handled.</item>
/// </list>
/// Dedupe is by <see cref="GitHubEventDedupe.KeyFor"/> through <see cref="IGitHubDeliveryStore"/>. A webhook for the same logical event
/// is matched with <see cref="GitHubEventDedupe.IsSameLogicalEvent"/> once webhook ingress shares the store (seam, not built here).
/// </summary>
public sealed class GitHubEventsPoller
{
    private static readonly Action<ILogger, string, int, Exception?> _logFailure = LoggerMessage.Define<string, int>(
        LogLevel.Warning, new EventId(10, "GitHubPollFailed"), "GitHub events poll for {Repo} failed (consecutive failures: {Failures}); backing off.");

    private static readonly Action<ILogger, string, DateTimeOffset, Exception?> _logRateLimited = LoggerMessage.Define<string, DateTimeOffset>(
        LogLevel.Warning, new EventId(11, "GitHubPollRateLimited"), "GitHub events poll for {Repo} is rate limited; waiting until {ResetAt:O}.");

    private static readonly Action<ILogger, string, int, Exception?> _logMalformed = LoggerMessage.Define<string, int>(
        LogLevel.Warning, new EventId(12, "GitHubPollMalformed"), "GitHub events page for {Repo} had {Count} unusable event(s); skipped.");

    private readonly IGitHubAdapter _adapter;
    private readonly IGitHubEventSink _sink;
    private readonly IGitHubDeliveryStore _store;
    private readonly GitHubEventMapper _mapper;
    private readonly GitHubIngressOptions _options;
    private readonly ILogger<GitHubEventsPoller> _logger;
    private readonly TimeProvider _time;
    private readonly IReadOnlySet<string> _memberLogins;
    private readonly List<RepoState> _repos;
    private readonly Dictionary<string, (DateTimeOffset UpdatedAt, string? HeadSha)> _applied = new(StringComparer.Ordinal);

    public GitHubEventsPoller(
        IGitHubAdapter adapter,
        IGitHubEventSink sink,
        IGitHubDeliveryStore store,
        GitHubIngressOptions options,
        ILogger<GitHubEventsPoller> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _adapter = adapter;
        _sink = sink;
        _store = store;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _mapper = new GitHubEventMapper(options.AgentLogins);
        _memberLogins = new HashSet<string>(options.MemberLogins.Concat(options.AgentLogins), StringComparer.OrdinalIgnoreCase);
        _repos = [.. options.ParsedRepositories().Select(r => new RepoState(r.Owner, r.Repo))];
    }

    /// <summary>The configured interval, floored at one minute.</summary>
    private TimeSpan ConfiguredInterval => TimeSpan.FromMinutes(Math.Max(1, _options.PollIntervalMinutes));

    /// <summary>
    /// Polls each due repository once and returns how long to sleep until the next one is due. Never throws except
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<TimeSpan> PollDueAsync(CancellationToken cancellationToken)
    {
        foreach (var repo in _repos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_time.GetUtcNow() < repo.NextPollAt)
            {
                continue;
            }

            try
            {
                await PollRepoAsync(repo, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GitHubRateLimitedException ex)
            {
                // Exhausted (or deferred at the 90% threshold): nothing is sent until GitHub's reset time.
                repo.NextPollAt = ex.ResetAt > _time.GetUtcNow() ? ex.ResetAt : _time.GetUtcNow() + ConfiguredInterval;
                _logRateLimited(_logger, repo.Full, repo.NextPollAt, null);
            }
            catch (Exception ex)
            {
                repo.Failures++;
                repo.NextPollAt = _time.GetUtcNow() + FailureBackoff(repo.Failures);
                _logFailure(_logger, repo.Full, repo.Failures, ex);
            }
        }

        var now = _time.GetUtcNow();
        var next = _repos.Count == 0 ? now + ConfiguredInterval : _repos.Min(r => r.NextPollAt);
        var wait = next - now;
        return wait < _options.MinLoopDelay ? _options.MinLoopDelay : wait;
    }

    private TimeSpan FailureBackoff(int failures)
    {
        var ticks = _options.FailureBackoffBase.Ticks * Math.Pow(2, Math.Min(failures - 1, 20));
        var backoff = TimeSpan.FromTicks((long)Math.Min(ticks, _options.MaxFailureBackoff.Ticks));
        return backoff < _options.FailureBackoffBase ? _options.FailureBackoffBase : backoff;
    }

    private async Task PollRepoAsync(RepoState repo, CancellationToken ct)
    {
        repo.NodeId ??= await _adapter.GetRepositoryNodeIdAsync(_options.InstallationId, repo.Owner, repo.Name, ct);

        var page = await _adapter.ListRepositoryEventsAsync(new RepositoryEventsRequest(_options.InstallationId, repo.Owner, repo.Name, repo.ETag), ct);

        // Never poll faster than GitHub asked, and never faster than the operator's interval.
        var interval = page.PollInterval is { } asked && asked > ConfiguredInterval ? asked : ConfiguredInterval;

        if (!page.NotModified)
        {
            await HandlePageAsync(repo, page.Body, ct);
            repo.ETag = page.ETag;
        }

        repo.Failures = 0;
        repo.NextPollAt = _time.GetUtcNow() + interval;
    }

    private async Task HandlePageAsync(RepoState repo, string body, CancellationToken ct)
    {
        var parsed = GitHubEventsApiParser.Parse(body, repo.NodeId!, _memberLogins);
        if (parsed.Malformed > 0)
        {
            _logMalformed(_logger, repo.Full, parsed.Malformed, null);
        }

        var now = _time.GetUtcNow();
        var cutoff = _options.MaxEventAge > TimeSpan.Zero ? now - _options.MaxEventAge : (DateTimeOffset?)null;

        // GitHub sends newest first. Apply oldest first so state moves forward; ids are monotonic, creation time breaks no ties here.
        var ordered = parsed.Events
            .OrderBy(e => e.UpdatedAt)
            .ThenBy(e => long.TryParse(e.EventId, out var n) ? n : long.MaxValue)
            .ThenBy(e => e.EventId, StringComparer.Ordinal);

        var seenInPage = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gitHubEvent in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (cutoff is { } limit && gitHubEvent.UpdatedAt < limit)
            {
                continue;
            }

            var key = GitHubEventDedupe.KeyFor(gitHubEvent);
            if (!seenInPage.Add(key) || await _store.IsProcessedAsync(IGitHubDeliveryStore.PollSource, gitHubEvent.EventId, ct))
            {
                continue;
            }

            var subject = gitHubEvent.PrNodeId;
            var apply = true;
            if (subject is not null && _applied.TryGetValue(subject, out var last))
            {
                apply = GitHubEventOrdering.Decide(gitHubEvent, last.UpdatedAt, last.HeadSha).Apply;
            }

            var effects = apply ? _mapper.Map(gitHubEvent) : [];
            await _sink.DeliverAsync(new GitHubEventDelivery(gitHubEvent, key, apply, effects), ct);
            await _store.MarkProcessedAsync(IGitHubDeliveryStore.PollSource, gitHubEvent.EventId, gitHubEvent.EventName, _time.GetUtcNow(), ct);

            if (apply && subject is not null)
            {
                RememberApplied(subject, gitHubEvent);
            }
        }
    }

    private void RememberApplied(string prNodeId, GitHubEvent gitHubEvent)
    {
        if (!_applied.ContainsKey(prNodeId) && _applied.Count >= _options.MaxTrackedPullRequests)
        {
            // Bounded memory. Forgetting a PR only means its next event is applied without an ordering check.
            _applied.Remove(_applied.Keys.First());
        }

        var previous = _applied.TryGetValue(prNodeId, out var last) ? last : default;
        _applied[prNodeId] = (gitHubEvent.UpdatedAt >= previous.UpdatedAt ? gitHubEvent.UpdatedAt : previous.UpdatedAt, gitHubEvent.HeadSha ?? previous.HeadSha);
    }

    private sealed class RepoState(string owner, string name)
    {
        public string Owner { get; } = owner;

        public string Name { get; } = name;

        public string Full { get; } = owner + "/" + name;

        public string? NodeId { get; set; }

        public string? ETag { get; set; }

        public int Failures { get; set; }

        public DateTimeOffset NextPollAt { get; set; } = DateTimeOffset.MinValue;
    }
}
