using Bazaar.Server.Host.Startup;

namespace Bazaar.Server.Host.Recovery;

/// <summary>
/// Doc 03 startup step 6 and the shutdown checkpoint. Registered right after <c>StartupSequence</c>, so the sweep runs after the instance lock
/// and schema gate and before the remaining hosted services start. The readiness check keeps <c>/health/ready</c> false until it finishes.
/// The sweep is idempotent (doc 06) and bounded by the host start token. A failed sweep fails the start rather than serving with stale leases.
/// </summary>
public sealed partial class RecoveryHostedService(
    ILeaseRecoveryRunner runner,
    IEnumerable<IShutdownCheckpointParticipant> participants,
    IOptions<RecoveryHookOptions> options,
    RecoveryHookState state,
    TimeProvider time,
    ILogger<RecoveryHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var report = await runner.RecoverAsync(cancellationToken).ConfigureAwait(false);
        state.SweepDone(report);
        LogSweep(logger, report.ExpiredLeases, report.TurnsAbandoned, report.StepsAbandoned, report.StepsUnknown, report.ItemsReleased);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(options.Value.ShutdownTimeout);
            foreach (var participant in participants)
            {
                try
                {
                    await participant.CheckpointAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogParticipantFailed(logger, ex, participant.GetType().Name);
                    if (cts.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            state.CheckpointDone(time.GetUtcNow());
            LogCheckpoint(logger, cts.IsCancellationRequested);
        }
        catch (Exception ex)
        {
            LogCheckpointFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Lease recovery sweep: {Expired} expired leases, {Turns} turns abandoned, {Abandoned} steps abandoned, {Unknown} steps unknown, {Released} inbox items released.")]
    private static partial void LogSweep(ILogger logger, int expired, int turns, int abandoned, int unknown, int released);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutdown checkpoint recorded (timed out: {TimedOut}).")]
    private static partial void LogCheckpoint(ILogger logger, bool timedOut);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown checkpoint participant {Participant} failed.")]
    private static partial void LogParticipantFailed(ILogger logger, Exception ex, string participant);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shutdown checkpoint failed.")]
    private static partial void LogCheckpointFailed(ILogger logger, Exception ex);
}

/// <summary>Not ready until the startup lease recovery sweep has finished.</summary>
public sealed class RecoverySweepReadinessCheck(RecoveryHookState state) : IReadinessCheck
{
    public string Name => "lease_recovery";

    public Task<ReadinessResult> CheckAsync(CancellationToken ct) =>
        Task.FromResult(new ReadinessResult(Name, state.SweepCompleted, state.SweepCompleted ? null : "sweep pending"));
}
