using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Community.Actions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Community.Drafts;

/// <summary>[PROPOSED] Expires pending drafts whose approval window has closed (doc 36 work package 8).</summary>
public interface ICommunityDraftExpirySweeper
{
    /// <summary>Expires every due draft and writes one <c>expired</c> action-log row for each. Returns how many drafts it expired.</summary>
    Task<int> SweepAsync(CancellationToken ct = default);
}

/// <summary>
/// Calls <see cref="ICommunityDraftStore.ExpireDueAsync"/> (one compare-and-set UPDATE, so a concurrent decision and a sweep cannot both
/// win), then appends one <c>expired</c> row per draft to the action log, with no body, like the handler's denial rows. The expiry is
/// committed before the rows are written; a draft whose row fails to write is counted in the log and skipped, and the sweep goes on.
/// </summary>
public sealed class CommunityDraftExpirySweeper(
    ICommunityDraftStore drafts,
    CommunityActionStore actions,
    TimeProvider? clock = null,
    ILogger<CommunityDraftExpirySweeper>? logger = null) : ICommunityDraftExpirySweeper
{
    /// <summary>Action-log status for a draft that expired unanswered.</summary>
    public const string ExpiredStatus = "expired";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var ids = await drafts.ExpireDueAsync(_clock.GetUtcNow(), ct).ConfigureAwait(false);
        var unrecorded = 0;
        foreach (var id in ids)
        {
            try
            {
                var draft = await drafts.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
                if (draft is null) continue;
                _ = await actions.AppendAsync(
                    new CommunityActionRecord(
                        Id: 0,
                        AgentId: draft.AgentId,
                        SponsorId: draft.SponsorId,
                        Platform: draft.Post.Platform,
                        Action: draft.Action,
                        TargetUrl: draft.Post.Target,
                        Body: null,
                        BodySha256: SHA256.HashData(Encoding.UTF8.GetBytes(draft.Post.Body)),
                        ApprovalId: draft.ApprovalId.ToString("D"),
                        TaintSourcesJson: draft.TaintSourcesJson,
                        ExternalId: null,
                        Status: ExpiredStatus,
                        CreatedAt: default),
                    CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One draft's log failure must not stop the others from being recorded.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                unrecorded++;
                ExpiryLog.RowNotWritten(logger, ex, id);
            }
        }

        if (unrecorded > 0) ExpiryLog.Unrecorded(logger, unrecorded);
        return ids.Count;
    }
}

/// <summary>
/// [PROPOSED] Runs the sweeper (resolved on each pass, so an unbound host logs a warning instead of failing startup) every <see cref="CommunityDraftOptions.SweepInterval"/>. A failed sweep is
/// logged and the loop carries on; only cancellation ends it.
/// </summary>
public sealed class CommunityDraftExpiryService(
    Func<ICommunityDraftExpirySweeper> sweeperFactory,
    CommunityDraftOptions options,
    TimeProvider? clock = null,
    ILogger<CommunityDraftExpiryService>? logger = null) : BackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = await sweeperFactory().SweepAsync(stoppingToken).ConfigureAwait(false);
                if (expired > 0) ExpiryLog.Expired(logger, expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // The loop must outlive any one failed sweep.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ExpiryLog.SweepFailed(logger, ex);
            }

            try
            {
                await Task.Delay(options.SweepInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
