using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Paths;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.VmMounts.Providers.Canaries;

/// <summary>Limits of the canary sink. Server-side values; nothing here comes from a guest.</summary>
public sealed class CanarySinkOptions
{
    /// <summary>Wait before the first background retry of a trip that has not finished.</summary>
    public TimeSpan RetryInitialDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest wait between background retries.</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Time limit of one containment call, so a hung host leg cannot hold the caller for ever.</summary>
    public TimeSpan ContainmentTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Distinct unfinished trips kept. A trip beyond it is refused loudly, never dropped silently.</summary>
    public int MaxPending { get; init; } = 1000;
}

/// <summary>The trip was received and queued but not every step has finished yet. It is retried in the background; the observer sees this error.</summary>
public sealed class CanaryTripPendingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The shipped <see cref="ICanarySink"/> (doc 15 "Credentials, mounts, browser": page and freeze, PROPOSED there). A canary use does three
/// things, each retried until it succeeds: an audit record of the trip, containment of the agent through <see cref="IAgentContainment"/>
/// (pause its VMs, revoke tokens, block egress, detach mounts), and an audit record of the containment outcome.
/// </summary>
/// <remarks>
/// <para>
/// A trip is never dropped. It is queued in memory first, then processed at once. Steps are independent: a failed audit write does not
/// delay containment, and a failed containment does not stop the audit. A step that fails is logged as an error, and the call throws
/// <see cref="CanaryTripPendingException"/> so the observer knows it is not finished; the background <see cref="CanaryTripRetryService"/>
/// keeps going until it is. Repeats of the same trip while it is pending share one queue entry.
/// </para>
/// <para>
/// Limits (reported gaps): the queue is in memory and is lost on a host restart (the manager's own <c>vm.mount.canary_use</c> audit row is
/// written first and the canary fingerprints are persisted, but a trip in flight at a crash is not replayed); there is no owner paging
/// seam in the abstractions, so "page" is the audit record plus the error log; and <see cref="IAgentContainment"/> pauses and revokes but
/// does not itself set the kill-switch stop, which has no abstraction an module may call. The containment is looked up when a trip happens,
/// not when the sink is built: agent safety's containment depends on the mount manager, which depends on this sink.
/// </para>
/// </remarks>
public sealed partial class ContainingCanarySink : ICanarySink
{
    internal const string Actor = "vm-mounts-canary";

    private readonly IAuditLog _audit;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _time;
    private readonly CanarySinkOptions _options;
    private readonly ILogger<ContainingCanarySink> _logger;
    private readonly ConcurrentDictionary<string, PendingTrip> _pending = new();

    public ContainingCanarySink(
        IAuditLog audit, IServiceProvider services, TimeProvider time, CanarySinkOptions options, ILogger<ContainingCanarySink> logger)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _audit = audit;
        _services = services;
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>Trips that have not finished every step.</summary>
    public int PendingCount => _pending.Count;

    internal CanarySinkOptions Options => _options;

    public async ValueTask ReportCanaryUseAsync(CanaryUseReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.AgentId == Guid.Empty)
            throw new ArgumentException("A canary report needs an agent id.", nameof(report));

        var key = $"{report.AgentId:N}|{report.MountId:N}|{report.CanaryId}|{report.Kind}|{report.RelativePath}";
        PendingTrip trip;
        if (_pending.TryGetValue(key, out var existing))
        {
            trip = existing;
        }
        else
        {
            if (_pending.Count >= _options.MaxPending)
            {
                LogQueueFull(_logger, report.AgentId);
                throw new CanaryTripPendingException("The canary trip queue is full; the trip was not queued.");
            }

            trip = _pending.GetOrAdd(key, _ => new PendingTrip(key, report));
        }

        // The caller's token is not used to stop containment: abandoning it would leave the agent running. The trip is already queued.
        await ProcessAsync(trip).ConfigureAwait(false);
        if (_pending.ContainsKey(key))
            throw new CanaryTripPendingException(
                $"The canary trip for agent {report.AgentId:D} is queued but not finished: {trip.LastFailure ?? "a step failed"}. It is retried in the background.");
    }

    /// <summary>Retries every pending trip once. Returns how many are still pending.</summary>
    public async Task<int> RetryPendingAsync()
    {
        foreach (var trip in _pending.Values)
            await ProcessAsync(trip).ConfigureAwait(false);
        return _pending.Count;
    }

    private async Task ProcessAsync(PendingTrip trip)
    {
        await trip.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            trip.Attempts++;
            trip.LastFailure = null;

            if (!trip.TripAudited)
                trip.TripAudited = await TryAuditAsync(trip, MountProviderAuditActions.CanaryTrip, null).ConfigureAwait(false);

            if (!trip.Contained)
            {
                try
                {
                    await ContainAsync(trip.Report).ConfigureAwait(false);
                    trip.Contained = true;
                    LogContained(_logger, trip.Report.AgentId, trip.Report.Kind);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    trip.LastFailure = $"containment failed ({ex.GetType().Name})";
                    LogContainmentFailed(_logger, ex, trip.Report.AgentId, trip.Attempts);
                    if (!trip.FailureAudited)
                        trip.FailureAudited = await TryAuditAsync(trip, MountProviderAuditActions.CanaryContainmentFailed, ex).ConfigureAwait(false);
                }
            }

            if (trip.Contained && !trip.ContainedAudited)
                trip.ContainedAudited = await TryAuditAsync(trip, MountProviderAuditActions.CanaryContained, null).ConfigureAwait(false);

            if (trip.TripAudited && trip.Contained && trip.ContainedAudited)
                _pending.TryRemove(trip.Key, out _);
            else
                trip.LastFailure ??= "an audit record could not be written";
        }
        finally
        {
            trip.Gate.Release();
        }
    }

    private async Task ContainAsync(CanaryUseReport report)
    {
        var containment = _services.GetService<IAgentContainment>()
            ?? throw new InvalidOperationException("No IAgentContainment is registered, so the agent cannot be contained.");
        using var limit = new CancellationTokenSource(_options.ContainmentTimeout);
        await containment.ContainAsync(report.AgentId.ToString("D"), Actor, limit.Token).ConfigureAwait(false);
    }

    private async Task<bool> TryAuditAsync(PendingTrip trip, string kind, Exception? failure)
    {
        var report = trip.Report;
        var payload = new JsonObject
        {
            ["agent_id"] = report.AgentId.ToString("D"),
            ["vm_id"] = report.VmId.ToString("D"),
            ["mount_id"] = report.MountId.ToString("D"),
            // The fingerprint of the canary value, never the value.
            ["canary_id"] = SafeText.Clean(report.CanaryId),
            ["path"] = SafeText.Clean(report.RelativePath),
            ["kind"] = report.Kind.ToString(),
            ["source"] = SafeText.Clean(report.Source),
            ["at"] = report.At.ToString("O"),
            ["retired"] = report.Retired,
            ["attempts"] = trip.Attempts,
        };
        if (failure is not null)
            payload["error"] = failure.GetType().Name;

        try
        {
            await _audit.AppendAsync(new AuditEntry(_time.GetUtcNow(), VmMountAuditActions.Actor, kind, payload.ToJsonString()), CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogAuditFailed(_logger, ex, kind, report.AgentId);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Canary trip for agent {AgentId} not queued: the queue is full.")]
    private static partial void LogQueueFull(ILogger logger, Guid agentId);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Canary use ({Kind}) contained agent {AgentId}.")]
    private static partial void LogContained(ILogger logger, Guid agentId, CanaryUseKind kind);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Canary use: containing agent {AgentId} failed on attempt {Attempt}; it will be retried.")]
    private static partial void LogContainmentFailed(ILogger logger, Exception ex, Guid agentId, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Canary use: the audit record {Kind} for agent {AgentId} could not be written; it will be retried.")]
    private static partial void LogAuditFailed(ILogger logger, Exception ex, string kind, Guid agentId);

    private sealed class PendingTrip(string key, CanaryUseReport report)
    {
        public string Key { get; } = key;

        public CanaryUseReport Report { get; } = report;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Attempts { get; set; }

        public string? LastFailure { get; set; }

        public bool TripAudited { get; set; }

        public bool Contained { get; set; }

        public bool ContainedAudited { get; set; }

        public bool FailureAudited { get; set; }
    }
}

/// <summary>Retries unfinished canary trips with growing waits until none is left. Errors are logged by the sink; this loop never stops on one.</summary>
internal sealed class CanaryTripRetryService(ContainingCanarySink sink, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = sink.Options.RetryInitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);
                if (sink.PendingCount == 0)
                {
                    wait = sink.Options.RetryInitialDelay;
                    continue;
                }

                var left = await sink.RetryPendingAsync().ConfigureAwait(false);
                wait = left == 0
                    ? sink.Options.RetryInitialDelay
                    : TimeSpan.FromTicks(Math.Min(sink.Options.RetryMaxDelay.Ticks, wait.Ticks * 2));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The sink has already logged each failed step; an unexpected throw here must not end the loop that keeps the trips alive.
                wait = sink.Options.RetryMaxDelay;
            }
        }
    }
}
