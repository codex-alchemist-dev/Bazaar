using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.VmEgress;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>
/// <see cref="IAgentContainment"/> decorator that adds the network and mount legs of the kill switch (doc 15: block egress, detach
/// mounts) to the inner containment, which revokes tokens and pauses VMs.
/// </summary>
/// <remarks>
/// Contain attempts every step whatever an earlier one did: egress block-all, mount detach with <see cref="DetachReason.KillSwitch"/>
/// (the mount manager scrubs the guest through <see cref="IGuestScrubber"/>), then the inner contain. Failures are audited and thrown
/// together as one <see cref="AggregateException"/>, so the stop stays in force and the failure is visible. Both legs are idempotent
/// in their modules, so a repeated contain is harmless. Release delegates to the inner adapter first, which verifies the signed owner
/// request; egress is released only when that returned <see cref="ContainmentReleaseOutcome.Released"/> or
/// <see cref="ContainmentReleaseOutcome.NotContained"/> (an authentic request for an agent whose earlier release failed half way, so
/// a retry can still lift the block). A denied request releases nothing. A release that leaves the block in place throws.
/// Mounts are not re-attached on release: the owner's next attach does that.
/// </remarks>
public sealed class NetworkAndMountContainment(
    IAgentContainment inner,
    IEgressPolicy egress,
    IMountManager mounts,
    IAuditLog audit,
    TimeProvider? timeProvider = null) : IAgentContainment
{
    /// <summary>Audit kind of the network and mount containment steps.</summary>
    public const string ContainKind = "containment.network_mounts";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task ContainAsync(string agentId, string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var failures = new List<Exception>();

        await StepAsync(failures, actorId, agentId, "egress_blocked", () => egress.BlockAllAsync(agentId, ct)).ConfigureAwait(false);
        await StepAsync(failures, actorId, agentId, "mounts_detached", async () =>
        {
            if (!Guid.TryParse(agentId, out var agent))
            {
                throw new ArgumentException("The agent id is not a GUID, so its mounts cannot be found.", nameof(agentId));
            }

            await mounts.DetachAllForAgentAsync(agent, DetachReason.KillSwitch, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await StepAsync(failures, actorId, agentId, "inner_contained", () => inner.ContainAsync(agentId, actorId, ct)).ConfigureAwait(false);

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
        EgressApplyResult released;
        try
        {
            released = await egress.ReleaseBlockAsync(agentId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

    private async Task StepAsync(List<Exception> failures, string actor, string agentId, string step, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            await TryAuditAsync(actor, agentId, step, "ok").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
            await TryAuditAsync(actor, agentId, step + "_failed", ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>Audits without letting an audit failure stop the remaining steps: the action already ran.</summary>
    private async Task TryAuditAsync(string actor, string agentId, string step, string detail)
    {
        try
        {
            var payload = new JsonObject { ["agent_id"] = agentId, ["step"] = step, ["detail"] = detail };
            await audit.AppendAsync(new AuditEntry(_time.GetUtcNow(), actor, ContainKind, payload.ToJsonString()), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Losing the audit row must not skip the next containment step.
        }
    }
}
