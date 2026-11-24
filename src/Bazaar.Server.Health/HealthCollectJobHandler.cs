using Bazaar.Server.Jobs;
using Bazaar.Server.Knowledge.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Health;

/// <summary>
/// The recurring health collection job (doc 25 WP1; update trigger "CI completion; nightly rollup"). For each project it reads the
/// day's facts from every registered <see cref="IHealthFactSource"/>, turns them into samples and upserts them. It collects today and
/// yesterday (UTC), so a CI run that finished after the last pass is picked up. Idempotent: the store replaces samples with the same
/// key, so a re-run (an expired lease) writes the same rows. One project failing does not stop the others; the job fails after the
/// pass so it is retried. With no <see cref="IHealthProjectSource"/> registered it does nothing.
/// </summary>
public sealed partial class HealthCollectJobHandler(
    IMetricStore store,
    IEnumerable<IHealthFactSource> sources,
    IEnumerable<IHealthProjectSource> projectSources,
    TimeProvider timeProvider,
    IEnumerable<IBuildBrokenListener>? buildBrokenListeners = null,
    ILogger<HealthCollectJobHandler>? logger = null) : IJobHandler
{
    /// <summary>The <c>job.kind</c> the poller enqueues on the collect cadence.</summary>
    public const string JobKind = "health.collect";

    private readonly BuildBrokenDetector _detector = new();
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health collection failed for project {Project} on {Day}.")]
    private static partial void LogCollectionFailed(ILogger logger, Exception exception, string project, DateOnly day);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A build-broken listener failed for project {Project}.")]
    private static partial void LogListenerFailed(ILogger logger, Exception exception, string project);

    public string Kind => JobKind;

    public async Task HandleAsync(JobRecord job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await CollectAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Runs one pass and returns how many samples were new. Exposed for tests and for a CI-completion trigger.</summary>
    public async Task<int> CollectAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var sourceList = sources.ToList();
        var added = 0;
        var failures = new List<Exception>();

        foreach (var projectSource in projectSources)
        {
            foreach (var project in await projectSource.ListProjectsAsync(ct).ConfigureAwait(false))
            {
                foreach (var day in new[] { today.AddDays(-1), today })
                {
                    try
                    {
                        var parts = new List<HealthFacts>();
                        foreach (var source in sourceList)
                            parts.Add(await source.ReadAsync(project, day, ct).ConfigureAwait(false));

                        var merged = HealthFacts.Merge(parts);
                        var samples = HealthSampleBuilder.Build(project, day, merged);
                        added += await store.UpsertAsync(samples, ct).ConfigureAwait(false);
                        await NotifyBuildBrokenAsync(project, merged.CiRuns, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogCollectionFailed(_log, ex, project, day);
                        failures.Add(ex);
                    }
                }
            }
        }

        if (failures.Count > 0)
            throw new AggregateException("Health collection failed for one or more projects.", failures);
        return added;
    }

    private async Task NotifyBuildBrokenAsync(string project, IReadOnlyList<CiRunFact> runs, CancellationToken ct)
    {
        foreach (var signal in _detector.Observe(project, runs))
        {
            foreach (var listener in buildBrokenListeners ?? [])
            {
                try
                {
                    await listener.OnBuildBrokenAsync(signal, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogListenerFailed(_log, ex, project);
                }
            }
        }
    }
}
