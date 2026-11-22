using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Identity.Leases;
using Bazaar.Server.Security;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.VmEgress;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Bazaar.Server.AgentSafety.Tests.NetworkAndMountContainmentTests;

namespace Bazaar.Server.AgentSafety.Tests;

public sealed class AgentSafetyCompositionTests
{
    private sealed class FakePauser(Log log) : IVmPauser
    {
        public Task PauseAsync(string agentId, CancellationToken ct = default)
        {
            log.Calls.Add("vms.pause");
            return Task.CompletedTask;
        }

        public Task ResumeAsync(string agentId, CancellationToken ct = default)
        {
            log.Calls.Add("vms.resume");
            return Task.CompletedTask;
        }
    }

    private sealed class RefusingScrubber : IGuestScrubber
    {
        public Task ScrubAsync(GuestScrubRequest request, CancellationToken ct) => throw new InvalidOperationException("refusing default");
    }

    public enum Order
    {
        ModuleOnly,
        AgentSafetyFirst,
        ScrubberDefaultFirst,
    }

    private sealed record Host(ServiceProvider Provider, Log Log, FakeAudit Audit, ECDsa OwnerKey) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            OwnerKey.Dispose();
        }
    }

    private static Host Compose(Order order, bool egress = true, bool mounts = true, bool lifecycle = false)
    {
        var log = new Log();
        var audit = new FakeAudit();
        var owner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var owners = new InMemoryOwnerKeyRegistry();
        owners.Register(new OwnerDeviceKey("owner-1", "dev-owner", owner.ExportSubjectPublicKeyInfo()));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IKillSwitchStateStore, InMemoryKillSwitchStateStore>();
        services.AddSingleton<IContainmentStateStore, InMemoryContainmentStateStore>();
        services.AddSingleton<INonceStore, InMemoryNonceStore>();
        services.AddSingleton<ITokenBudgetStore, InMemoryTokenBudgetStore>();
        services.AddSingleton<IAuditLog>(audit);
        services.AddSingleton<ICredentialLeaseStore>(new InMemoryCredentialLeaseStore(TimeProvider.System));
        services.AddSingleton<IOwnerKeyRegistry>(owners);
        services.AddSingleton<IVmPauser>(new FakePauser(log));
        services.AddSingleton<IAgentTokenRevoker>(new FakeRevoker(log));
        if (egress)
        {
            services.AddSingleton<IEgressPolicy>(new FakeEgress(log));
        }

        if (mounts)
        {
            services.AddSingleton<IMountManager>(new FakeMounts(log));
        }

        if (lifecycle)
        {
            services.AddSingleton(new VmLifecycleService(
                new VmGuestScrubberTests.FakeVmStore(), new VmGuestScrubberTests.FakeBackend(), new VmOperationLocks(), TimeProvider.System));
        }

        var config = new ConfigurationBuilder().Build();
        if (order == Order.AgentSafetyFirst)
        {
            services.AddAgentSafety(config);
        }

        if (order == Order.ScrubberDefaultFirst)
        {
            services.AddSingleton<IGuestScrubber, RefusingScrubber>();
        }

        services.AddBazaarAgentSafetyModule(config);
        return new Host(services.BuildServiceProvider(), log, audit, owner);
    }

    private static async Task<ContainmentReleaseRequest> SignedAsync(IAgentContainment containment, ECDsa key, string agent)
    {
        var challenge = await containment.IssueReleaseChallengeAsync(agent, "dev-owner", TestContext.Current.CancellationToken);
        Assert.NotNull(challenge);
        var signature = key.SignData(
            ApprovalSignature.ReleaseMessage(agent, "dev-owner", challenge.Nonce, challenge.ExpiresAt),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new ContainmentReleaseRequest(agent, "dev-owner", challenge.Nonce, challenge.ExpiresAt, signature);
    }

    [Theory]
    [InlineData(Order.ModuleOnly)]
    [InlineData(Order.AgentSafetyFirst)]
    [InlineData(Order.ScrubberDefaultFirst)]
    public void The_decorator_and_scrubber_registrations_survive_any_registration_order(Order order)
    {
        using var host = Compose(order);

        Assert.IsType<NetworkAndMountContainment>(host.Provider.GetRequiredService<IAgentContainment>());
        Assert.IsType<UnavailableGuestScrubber>(host.Provider.GetRequiredService<IGuestScrubber>());
    }

    [Fact]
    public void With_the_vm_lifecycle_composed_the_scrubber_is_the_real_one_whatever_the_order()
    {
        using var host = Compose(Order.ScrubberDefaultFirst, lifecycle: true);

        Assert.IsType<VmGuestScrubber>(host.Provider.GetRequiredService<IGuestScrubber>());
    }

    [Fact]
    public async Task Engage_and_release_run_end_to_end_with_the_egress_and_mount_modules_composed()
    {
        using var host = Compose(Order.ModuleOnly);
        var containment = host.Provider.GetRequiredService<IAgentContainment>();
        var agent = Guid.NewGuid().ToString();

        await containment.ContainAsync(agent, "owner-1", TestContext.Current.CancellationToken);

        Assert.Equal(["tokens.revoke", "egress.block", "mounts.detach", "tokens.revoke", "vms.pause"], host.Log.Calls);

        host.Log.Calls.Clear();
        var result = await containment.ReleaseAsync(agent, await SignedAsync(containment, host.OwnerKey, agent), TestContext.Current.CancellationToken);

        Assert.Equal(ContainmentReleaseOutcome.Released, result.Outcome);
        Assert.Equal(["vms.resume", "egress.release"], host.Log.Calls);
    }

    [Fact]
    public async Task On_a_host_without_egress_and_mounts_engage_reports_the_gap_and_release_still_completes()
    {
        using var host = Compose(Order.ModuleOnly, egress: false, mounts: false);
        var containment = host.Provider.GetRequiredService<IAgentContainment>();
        var agent = Guid.NewGuid().ToString();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => containment.ContainAsync(agent, "owner-1", TestContext.Current.CancellationToken));
        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains("vms.pause", host.Log.Calls); // the legs that exist still ran

        host.Log.Calls.Clear();
        var result = await containment.ReleaseAsync(agent, await SignedAsync(containment, host.OwnerKey, agent), TestContext.Current.CancellationToken);

        Assert.Equal(ContainmentReleaseOutcome.Released, result.Outcome);
        Assert.Equal(["vms.resume"], host.Log.Calls);
        Assert.Contains(host.Audit.Entries, e => e.Contains("egress_release_skipped"));

        // A repeated release (the engine retrying) finds nothing contained and still does not fail.
        var again = await containment.ReleaseAsync(agent, await SignedAsync(containment, host.OwnerKey, agent), TestContext.Current.CancellationToken);
        Assert.Equal(ContainmentReleaseOutcome.NotContained, again.Outcome);
    }
}
