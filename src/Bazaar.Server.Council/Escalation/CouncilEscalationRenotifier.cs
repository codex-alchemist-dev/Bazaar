using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Council.Escalation;

/// <summary>
/// One pass of the owner re-notify (doc 10, Escalation: no timeout decision, re-notify every
/// <c>council.escalation_renotify_hours</c>). A vote that was never alerted is alerted at once; after that, once per interval
/// until it is resolved. The last-alert times live in memory, so a restart alerts every open escalation once more, which is
/// the safe side. With no <see cref="ICouncilOwnerNotifier"/> registered the pass does nothing.
/// </summary>
public sealed partial class CouncilEscalationRenotifier(
    CouncilEscalationService escalations,
    TimeProvider clock,
    CouncilEscalationOptions options,
    ILogger<CouncilEscalationRenotifier> logger,
    ICouncilOwnerNotifier? notifier = null,
    ICouncilEscalationRenotifySource? hoursSource = null)
{
    private readonly Dictionary<Guid, DateTimeOffset> _lastAlert = [];

    /// <summary>Runs one pass and returns how many alerts were sent. A failed alert is logged and retried next pass.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (notifier is null)
        {
            return 0;
        }

        var hours = hoursSource is null ? options.RenotifyHours : await hoursSource.GetRenotifyHoursAsync(ct).ConfigureAwait(false);
        var interval = TimeSpan.FromHours(Math.Max(1, hours));
        var now = clock.GetUtcNow();
        var open = await escalations.ListAwaitingAsync(options.BatchSize, ct).ConfigureAwait(false);
        var sent = 0;
        foreach (var vote in open)
        {
            if (_lastAlert.TryGetValue(vote.VoteId, out var last) && now - last < interval)
            {
                continue;
            }

            try
            {
                await notifier.NotifyEscalationAsync(vote, ct).ConfigureAwait(false);
                _lastAlert[vote.VoteId] = now;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogNotifyFailed(logger, vote.VoteId, ex);
            }
        }

        var openIds = open.Select(v => v.VoteId).ToHashSet();
        foreach (var gone in _lastAlert.Keys.Where(k => !openIds.Contains(k)).ToList())
        {
            _ = _lastAlert.Remove(gone);
        }

        return sent;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Owner alert for escalated council vote {VoteId} failed.")]
    private static partial void LogNotifyFailed(ILogger logger, Guid voteId, Exception ex);
}

/// <summary>Runs <see cref="CouncilEscalationRenotifier.RunOnceAsync"/> every <see cref="CouncilEscalationOptions.Interval"/>.</summary>
public sealed partial class CouncilEscalationRenotifyWorker(
    CouncilEscalationRenotifier renotifier,
    CouncilEscalationOptions options,
    TimeProvider clock,
    ILogger<CouncilEscalationRenotifyWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await renotifier.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(options.Interval, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogPassFailed(logger, ex);
                try
                {
                    await Task.Delay(options.Interval, clock, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Council escalation re-notify pass failed.")]
    private static partial void LogPassFailed(ILogger logger, Exception ex);
}
