using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit.Checkpoints;
using Bazaar.Server.Security.Audit.Db;

namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// Anchors signed checkpoints to every configured target (design doc 48, section 5.3). It hashes <see cref="AuditCheckpoint.SignedBytes"/>
/// with SHA-256, asks each target for a receipt, verifies it, and stores it.
/// </summary>
/// <remarks>
/// A target failure is recorded as an <c>audit.anchor.failed</c> event and is not thrown, so the audit chain keeps writing. The failed
/// target is anchored again on the next call for the same checkpoint. A success then records <c>audit.anchor.recovered</c>. Failure
/// state is kept in memory, so a restart between a failure and its recovery loses the recovered event, not the receipt.
/// Host cancellation is not swallowed. A failure to write the audit event itself is not swallowed either: an unrecorded failure
/// would hide a gap.
/// </remarks>
public sealed class AuditAnchorService
{
    /// <summary>Audit kind for a target that did not anchor or verify a checkpoint.</summary>
    public const string FailedKind = "audit.anchor.failed";

    /// <summary>Audit kind for a target that anchored a checkpoint after an earlier failure.</summary>
    public const string RecoveredKind = "audit.anchor.recovered";

    private const string _actor = "system";
    private const string _reasonTimeout = "timeout";
    private const string _reasonNotVerified = "receipt_not_verified";

    private readonly IReadOnlyList<IAuditAnchorTarget> _targets;
    private readonly IAuditAnchorStore _store;
    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;
    private readonly HashSet<(long LastSeq, string TargetId)> _failed = [];
    private readonly Lock _gate = new();

    /// <summary>Creates the service. Throws when there are no targets, or the timeout is outside its bounds.</summary>
    public AuditAnchorService(
        IReadOnlyList<IAuditAnchorTarget> targets,
        IAuditAnchorStore store,
        IAuditLog auditLog,
        TimeProvider time,
        TimeSpan targetTimeout)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(time);
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one anchor target is required.", nameof(targets));
        }

        if (targetTimeout < AuditAnchorOptions.MinTargetTimeout || targetTimeout > AuditAnchorOptions.MaxTargetTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(targetTimeout), targetTimeout, "Timeout is outside the allowed bounds.");
        }

        _targets = targets;
        _store = store;
        _auditLog = auditLog;
        _time = time;
        _timeout = targetTimeout;
    }

    /// <summary>
    /// Anchors <paramref name="stored"/>'s checkpoint to every target that has no receipt for it yet. Targets that already have one
    /// are skipped, so calling this again is how a failed target is retried.
    /// </summary>
    public async Task<AuditAnchorOutcome> AnchorAsync(StoredAuditCheckpoint stored, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var checkpoint = stored.Checkpoint;
        var digest = SHA256.HashData(checkpoint.SignedBytes());

        var existing = (await _store.ReadForCheckpointAsync(checkpoint, ct).ConfigureAwait(false))
            .Select(receipt => receipt.TargetId)
            .ToHashSet(StringComparer.Ordinal);

        var anchored = new List<string>();
        var failed = new List<string>();
        foreach (var target in _targets)
        {
            if (existing.Contains(target.TargetId))
            {
                continue;
            }

            try
            {
                var receipt = await AnchorOneAsync(target, digest, ct).ConfigureAwait(false);
                await _store.AppendAsync(checkpoint, receipt, ct).ConfigureAwait(false);
                anchored.Add(target.TargetId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add(target.TargetId);
                MarkFailed(checkpoint.LastSeq, target.TargetId);
                await AppendEventAsync(FailedKind, checkpoint.LastSeq, target.TargetId, ReasonFor(ex), ct).ConfigureAwait(false);
                continue;
            }

            if (TakeFailure(checkpoint.LastSeq, target.TargetId))
            {
                await AppendEventAsync(RecoveredKind, checkpoint.LastSeq, target.TargetId, reason: null, ct).ConfigureAwait(false);
            }
        }

        return new AuditAnchorOutcome(anchored, failed);
    }

    private async Task<AuditAnchorReceipt> AnchorOneAsync(IAuditAnchorTarget target, byte[] digest, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);

        var receipt = await target.AnchorAsync(digest, deadline.Token).ConfigureAwait(false);
        if (receipt.TargetId != target.TargetId
            || !await target.VerifyAsync(receipt, digest, deadline.Token).ConfigureAwait(false))
        {
            throw new AnchorNotVerifiedException();
        }

        return receipt;
    }

    private void MarkFailed(long lastSeq, string targetId)
    {
        lock (_gate)
        {
            _failed.Add((lastSeq, targetId));
        }
    }

    private bool TakeFailure(long lastSeq, string targetId)
    {
        lock (_gate)
        {
            return _failed.Remove((lastSeq, targetId));
        }
    }

    private Task AppendEventAsync(string kind, long lastSeq, string targetId, string? reason, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["last_seq"] = lastSeq,
            ["target_id"] = targetId,
        };
        if (reason is not null)
        {
            payload["reason"] = reason;
        }

        return _auditLog.AppendAsync(new AuditEntry(_time.GetUtcNow(), _actor, kind, payload.ToJsonString()), ct);
    }

    /// <summary>
    /// Maps a failure to a short fixed reason. The exception message is not used, because HTTP and git errors can echo URLs.
    /// </summary>
    private static string ReasonFor(Exception ex) => ex switch
    {
        OperationCanceledException => _reasonTimeout,
        AnchorNotVerifiedException => _reasonNotVerified,
        _ => ex.GetType().Name,
    };

    /// <summary>Raised inside the service when a target's receipt does not verify. It is recorded as a failure, never thrown out.</summary>
    private sealed class AnchorNotVerifiedException : Exception
    {
    }
}

/// <summary>
/// The result of one <see cref="AuditAnchorService.AnchorAsync"/> call: the targets that anchored the checkpoint in this call, and
/// the targets that failed. Targets that already held a receipt are in neither list.
/// </summary>
public sealed record AuditAnchorOutcome(IReadOnlyList<string> Anchored, IReadOnlyList<string> Failed);
