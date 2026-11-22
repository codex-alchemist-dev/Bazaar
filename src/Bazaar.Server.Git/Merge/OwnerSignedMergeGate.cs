using System.Buffers;
using System.Text;
using System.Text.Json;
using Bazaar.Approvals;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Git.Merge;

/// <summary>
/// Provider-neutral owner-signed merge gate (doc 52, modelled on the GitHub gate). Checks run in this order and the first
/// failure refuses, before any executor call: the approval and merge name the same head sha; the approval is for a squash merge;
/// its args hash is this PR and merge; the owner signature verifies; the forge head is still that sha; every check on the head
/// is Success or Skipped (Pending fails) unless <see cref="GitMergeOptions.RequireGreenChecks"/> is off. Every outcome is audited.
/// A head with no checks at all passes the green-checks test (nothing is failing).
/// </summary>
public sealed class OwnerSignedMergeGate : IOwnerSignedMergeGate
{
    public const string MergedKind = "git.merge.merged";
    public const string RefusedKind = "git.merge.refused";
    public const string FailedKind = "git.merge.failed";

    private readonly IForgeRegistry _registry;
    private readonly IOwnerSignatureVerifier _verifier;
    private readonly IAuditLog _audit;
    private readonly GitMergeOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public OwnerSignedMergeGate(
        IForgeRegistry registry,
        IOwnerSignatureVerifier verifier,
        IAuditLog audit,
        GitMergeOptions? options = null,
        TimeProvider? time = null,
        ILogger<OwnerSignedMergeGate>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? new GitMergeOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<OwnerSignedMergeGate>.Instance;
    }

    public async Task MergeAsync(OwnerMergeApproval approval, MergeRequest merge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(merge);

        var payload = approval.Payload;
        try
        {
            if (!string.Equals(payload.HeadSha, merge.HeadSha, StringComparison.OrdinalIgnoreCase))
                throw new MergeNotApprovedException("the approval and the merge name different head shas", "head_mismatch");

            if (payload.Action != GitMergeArgs.Action)
                throw new MergeNotApprovedException("the approval is not for a squash merge", "wrong_action");

            if (!payload.ArgsHash.AsSpan().SequenceEqual(GitMergeArgs.Hash(approval.Pr, merge)))
                throw new MergeNotApprovedException("the approval is for a different merge", "args_mismatch");

            var verdict = await _verifier.VerifyAsync(payload, approval.Signature, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (verdict != OwnerSignatureVerdict.Valid)
                throw new MergeNotApprovedException($"the owner's signature was refused ({verdict})", "bad_signature");

            var forge = _registry.Resolve(approval.Pr.Provider);
            var current = await forge.GetHeadShaAsync(approval.Pr, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, payload.HeadSha, StringComparison.OrdinalIgnoreCase))
                throw new MergeNotApprovedException("the PR head moved after approval, so the owner must approve again", "head_moved");

            if (_options.RequireGreenChecks)
            {
                var checks = await forge.GetChecksAsync(approval.Pr, merge.HeadSha, cancellationToken).ConfigureAwait(false);
                foreach (var check in checks)
                {
                    var onHead = string.Equals(check.HeadSha, merge.HeadSha, StringComparison.OrdinalIgnoreCase);
                    if (!onHead || check.State is not (CheckState.Success or CheckState.Skipped))
                        throw new MergeNotApprovedException($"check '{check.Name}' is not green ({(onHead ? check.State.ToString() : "wrong head")})", "checks_not_green");
                }
            }

            var executor = _registry.ResolveMergeExecutor(approval.Pr.Provider);
            await executor.SquashMergeAsync(approval.Pr, merge, cancellationToken).ConfigureAwait(false);
        }
        catch (MergeNotApprovedException refusal)
        {
            await AuditAsync(RefusedKind, approval, merge, refusal.Code, refusal.Reason, propagate: true, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A forge or executor outage. The merge is not known to have happened, so say so and let the caller see the failure.
            await AuditAsync(FailedKind, approval, merge, "forge_error", ex.GetType().Name, propagate: false, cancellationToken).ConfigureAwait(false);
            throw;
        }

        // The merge is done. A failing audit sink must not turn a real merge into an error the caller would retry.
        await AuditAsync(MergedKind, approval, merge, "merged", null, propagate: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task AuditAsync(string kind, OwnerMergeApproval approval, MergeRequest merge, string code, string? reason, bool propagate, CancellationToken cancellationToken)
    {
        var entry = new AuditEntry(_time.GetUtcNow(), approval.Payload.HumanId.ToString("D"), kind, BuildPayload(approval, merge, code, reason));
        try
        {
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!propagate && ex is not OperationCanceledException)
        {
            GitLog.AuditFailed(_logger, ex, kind, approval.Pr.Provider, approval.Pr.Number);
        }
    }

    private static string BuildPayload(OwnerMergeApproval approval, MergeRequest merge, string code, string? reason)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("provider", approval.Pr.Provider);
            w.WriteString("repo", approval.Pr.FullName);
            w.WriteNumber("number", approval.Pr.Number);
            w.WriteString("headSha", merge.HeadSha);
            w.WriteString("approvalId", approval.Payload.ApprovalId.ToString("D"));
            w.WriteString("outcome", code);
            if (reason is not null)
                w.WriteString("reason", reason);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
