using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Security.Approvals;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Hive.Approvals;

/// <summary>
/// Applies the grant changes the owner approved. Each pass looks at every ledger entry: an approved one is applied through
/// <see cref="HiveGrantApplier"/> with the deciding owner as the grantor and then consumed, so it cannot run twice; a denied,
/// expired, consumed or missing one is forgotten; a pending one waits. A change that fails to apply stays for the next pass.
/// </summary>
public sealed partial class HiveGrantApprovalSweeper(
    IHiveGrantChangeLedger ledger,
    IServiceProvider services,
    TimeProvider time,
    ILogger<HiveGrantApprovalSweeper> log) : BackgroundService
{
    /// <summary>How often the ledger is checked.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>Runs one pass and returns how many changes were applied.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        // The approvals service and the Hive applier come from other components. Until the host has registered them there is
        // nothing to read a decision from, so the pass does nothing and the ledger keeps its entries.
        if (services.GetService(typeof(IApprovalStore)) is not IApprovalStore store
            || services.GetService(typeof(ApprovalService)) is not ApprovalService approvals
            || services.GetService(typeof(HiveGrantApplier)) is not HiveGrantApplier applier)
        {
            return 0;
        }

        var applied = 0;
        foreach (var (id, change) in await ledger.SnapshotAsync(ct).ConfigureAwait(false))
        {
            var row = await store.GetAsync(id, ct).ConfigureAwait(false);
            if (row is null || row.Tool != OwnerQueueHiveGrantApprovals.ToolName)
            {
                await ledger.RemoveAsync(id, ct).ConfigureAwait(false);
                continue;
            }

            var now = time.GetUtcNow();
            switch (row.Status)
            {
                case ApprovalStatus.Pending when now >= row.ExpiresAt:
                case ApprovalStatus.Approved when now >= row.ExpiresAt:
                case ApprovalStatus.Denied:
                case ApprovalStatus.Expired:
                case ApprovalStatus.Consumed:
                    await ledger.RemoveAsync(id, ct).ConfigureAwait(false);
                    break;
                case ApprovalStatus.Approved:
                    try
                    {
                        await applier.ApplyApprovedAsync(change, row.DecidedBy ?? "owner", ct).ConfigureAwait(false);
                        await approvals.ConsumeAsync(id, now, ct).ConfigureAwait(false);
                        await ledger.RemoveAsync(id, ct).ConfigureAwait(false);
                        applied++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogApplyFailed(ex, id);
                    }

                    break;
            }
        }

        return applied;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogSweepFailed(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hive grant change {Ticket} was approved but could not be applied; it will be retried.")]
    private partial void LogApplyFailed(Exception ex, Guid ticket);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hive grant approval sweep failed.")]
    private partial void LogSweepFailed(Exception ex);
}
