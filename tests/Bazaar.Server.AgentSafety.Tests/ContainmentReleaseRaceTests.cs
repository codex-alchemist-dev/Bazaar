using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.AgentSafety.Tests;

/// <summary>The re-containment sweep must not pause VMs that an owner release has just resumed but not yet removed from the store.</summary>
public sealed class ContainmentReleaseRaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records the VM state per agent and holds the resume until the test lets it go.</summary>
    private sealed class GatedPauser : IVmPauser
    {
        public TaskCompletionSource ResumeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ResumeMayFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Running { get; private set; }

        public Task PauseAsync(string agentId, CancellationToken ct = default)
        {
            Running = false;
            return Task.CompletedTask;
        }

        public async Task ResumeAsync(string agentId, CancellationToken ct = default)
        {
            Running = true;
            ResumeEntered.TrySetResult();
            await ResumeMayFinish.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private sealed class NoopRevoker : IAgentTokenRevoker
    {
        public Task RevokeAsync(string agentId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<TokenResumeResult> ResumeAsync(string agentId, CancellationToken ct = default) => Task.FromResult(TokenResumeResult.NotReversible);
    }

    [Fact]
    public async Task A_sweep_during_a_release_leaves_the_resumed_vms_running_and_the_release_completes()
    {
        var guard = new ContainmentReleaseGuard();
        var store = new InMemoryContainmentStateStore();
        var pauser = new GatedPauser();
        var revoker = new NoopRevoker();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var owners = new InMemoryOwnerKeyRegistry();
        owners.Register(new OwnerDeviceKey("owner-1", "dev-owner", key.ExportSubjectPublicKeyInfo()));
        var adapter = new AgentContainmentAdapter(pauser, revoker, owners, new ListAuditLog(), new InMemoryNonceStore(), store, TimeProvider.System, guard);
        var enforcer = new ContainmentEnforcer(store, revoker, pauser, NullLogger<ContainmentEnforcer>.Instance, releaseGuard: guard);
        await adapter.ContainAsync("agent-a", "owner-1", Ct);

        var challenge = await adapter.IssueReleaseChallengeAsync("agent-a", "dev-owner", Ct);
        Assert.NotNull(challenge);
        var signature = key.SignData(
            ApprovalSignature.ReleaseMessage("agent-a", "dev-owner", challenge.Nonce, challenge.ExpiresAt),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var release = adapter.ReleaseAsync("agent-a", new ContainmentReleaseRequest("agent-a", "dev-owner", challenge.Nonce, challenge.ExpiresAt, signature), Ct);

        // The VMs are resumed but the agent is still in the store: exactly the window the sweep must not act in.
        await pauser.ResumeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Single(await store.LoadAsync(Ct));
        var failed = await enforcer.EnforceOnceAsync(Ct);

        Assert.Empty(failed!);
        Assert.True(pauser.Running, "The sweep paused VMs that the release had resumed.");

        pauser.ResumeMayFinish.SetResult();
        var result = await release.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ContainmentReleaseOutcome.Released, result.Outcome);
        Assert.Empty(await store.LoadAsync(Ct));

        // Once the release is done the agent is out of the store, so later sweeps leave it alone.
        await enforcer.EnforceOnceAsync(Ct);
        Assert.True(pauser.Running);
    }
}
