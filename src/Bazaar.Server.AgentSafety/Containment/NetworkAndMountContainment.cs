using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.VmEgress;
using Bazaar.Server.VmMounts.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// <see cref="IAgentContainment"/> decorator that adds the network and mount legs of the kill switch (doc 15: revoke leases, block
/// egress, detach mounts, pause VMs) around the inner containment.
/// </summary>
/// <remarks>
/// <para>
/// Contain runs, in this order: revoke tokens, block egress, detach mounts with <see cref="DetachReason.KillSwitch"/> (the mount manager
/// scrubs the guest through <see cref="IGuestScrubber"/>), then the inner contain, which records the agent as contained and pauses its
/// VMs (and revokes tokens again, which is idempotent). Every leg is attempted whatever an earlier one did. A leg runs with
/// <see cref="CancellationToken.None"/> and its own <see cref="ContainmentOptions.LegTimeout"/>, never the caller's token, so a dropped
/// request cannot leave an agent half contained. Failures, including failed audit writes (also logged), are thrown together as one
/// <see cref="AggregateException"/> so the stop stays in force and the gap is visible. A module that is not composed (no
/// <see cref="IEgressPolicy"/> or <see cref="IMountManager"/>) is a loud failure of that leg on contain.
/// </para>
/// <para>
/// Release delegates to the inner adapter first, which verifies the signed owner request. Egress is lifted only when that returned
/// <see cref="ContainmentReleaseOutcome.Released"/> or <see cref="ContainmentReleaseOutcome.NotContained"/> (so a retry after a
/// half-failed release can still lift the block); a denied request lifts nothing. If the egress module is not composed there is
/// nothing to lift: the release proceeds and audits <c>egress_release_skipped</c>. A release that leaves the block in place throws.
/// An audit failure on release is logged, not thrown, because the lift already happened. Mounts are not re-attached on release.
/// </para>
/// </remarks>
public sealed partial class NetworkAndMountContainment(
    IAgentContainment inner,
    IAgentTokenRevoker revoker,
    IEgressPolicy? egress,
    IMountManager? mounts,
    IAuditLog audit,
    ContainmentOptions? options = null,
    TimeProvider? timeProvider = null,
    ILogger<NetworkAndMountContainment>? logger = null) : IAgentContainment
{
    /// <summary>Audit kind of the network and mount containment steps.</summary>
    public const string ContainKind = "containment.network_mounts";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _timeout = (options ?? new ContainmentOptions()).LegTimeout;
    private readonly ILogger _log = logger ?? NullLogger<NetworkAndMountContainment>.Instance;

    /// <inheritdoc />
    public async Task ContainAsync(string agentId, string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var failures = new List<Exception>();

        await StepAsync(failures, actorId, agentId, "tokens_revoked", t => revoker.RevokeAsync(agentId, t)).ConfigureAwait(false);
        await StepAsync(failures, actorId, agentId, "egress_blocked", t =>
            egress is null ? throw new InvalidOperationException("No egress policy is composed, so the agent's egress cannot be blocked.") : egress.BlockAllAsync(agentId, t))
            .ConfigureAwait(false);
        await StepAsync(failures, actorId, agentId, "mounts_detached", async t =>
        {
            if (mounts is null)
            {
                throw new InvalidOperationException("No mount manager is composed, so the agent's mounts cannot be detached.");
            }

            if (!Guid.TryParse(agentId, out var agent))
            {
                throw new ArgumentException("The agent id is not a GUID, so its mounts cannot be found.", nameof(agentId));
            }

            await mounts.DetachAllForAgentAsync(agent, DetachReason.KillSwitch, t).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await StepAsync(failures, actorId, agentId, "inner_contained", t => inner.ContainAsync(agentId, actorId, t)).ConfigureAwait(false);

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("Containment did not complete; the stop stays in force.", failures);
        }
    }

    /// <inheritdoc />
    public Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default) =>
        inner.IssueReleaseChallengeAsync(agentId, deviceId, ct);

    /// <inheritdoc />
    public async Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default)
    {
        var result = await inner.ReleaseAsync(agentId, request, ct).ConfigureAwait(false);
        if (result.Outcome == ContainmentReleaseOutcome.Denied)
        {
            return result;
        }

        var actor = request.DeviceId ?? "unknown";
        if (egress is null)
        {
            await TryAuditAsync(actor, agentId, "egress_release_skipped", "No egress policy is composed; there is no egress block to lift.").ConfigureAwait(false);
            return result;
        }

        using var leg = new CancellationTokenSource(_timeout, _time);
        EgressApplyResult released;
        try
        {
            released = await egress.ReleaseBlockAsync(agentId, leg.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryAuditAsync(actor, agentId, "egress_release_failed", ex.Message).ConfigureAwait(false);
            throw;
        }

        if (!released.IsApplied)
        {
            await TryAuditAsync(actor, agentId, "egress_release_refused", $"{released.Outcome}: {released.Detail}").ConfigureAwait(false);
            throw new InvalidOperationException($"Egress release was not applied ({released.Outcome}); the block stays in force.");
        }

        await TryAuditAsync(actor, agentId, "egress_released", released.Detail ?? "Egress rules restored.").ConfigureAwait(false);
        return result;
    }

    /// <summary>Runs one leg under its own timeout and no caller cancellation, audits it, and records failures (audit failures too).</summary>
    private async Task StepAsync(List<Exception> failures, string actor, string agentId, string step, Func<CancellationToken, Task> action)
    {
        using var leg = new CancellationTokenSource(_timeout, _time);
        string outcome;
        try
        {
            await action(leg.Token).ConfigureAwait(false);
            outcome = step;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            outcome = step + "_failed";
            if (await TryAuditAsync(actor, agentId, outcome, ex.Message).ConfigureAwait(false) is { } auditFailure)
            {
                failures.Add(auditFailure);
            }

            return;
        }

        if (await TryAuditAsync(actor, agentId, outcome, "ok").ConfigureAwait(false) is { } failure)
        {
            failures.Add(failure);
        }
    }

    /// <summary>Appends an audit row. A failure is logged and returned (never thrown), so the next step still runs.</summary>
    private async Task<Exception?> TryAuditAsync(string actor, string agentId, string step, string detail)
    {
        try
        {
            var payload = new JsonObject { ["agent_id"] = agentId, ["step"] = step, ["detail"] = detail };
            await audit.AppendAsync(new AuditEntry(_time.GetUtcNow(), actor, ContainKind, payload.ToJsonString()), CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            LogAuditFailed(_log, ex, agentId, step);
            return ex;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Containment audit write failed for agent {AgentId} at step {Step}.")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception, string agentId, string step);
}
