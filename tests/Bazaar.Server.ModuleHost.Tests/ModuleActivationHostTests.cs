using System.Security.Cryptography;
using Bazaar.Contracts.Modules;
using Bazaar.Core;
using Bazaar.Install;
using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;
using Bazaar.ModulePermissions;
using Bazaar.Server.ModuleHost;
using Bazaar.Server.Modules.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.ModuleHost.Tests;

public sealed class ModuleActivationHostTests
{
    private const string _publisher = "publisher-1";
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class Packages(params ModulePackage[] packages) : IModulePackageSource
    {
        public ValueTask<ModulePackage?> FindAsync(string moduleId, CancellationToken ct = default) =>
            ValueTask.FromResult(packages.FirstOrDefault(p => p.ModuleId == moduleId));
    }

    private sealed class Store(params ModulePermissionApprovalRecord[] rows) : IModuleInstallStore
    {
        public Task RecordInstalledAsync(InstallAuditEntry entry, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RecordPermissionApprovalAsync(ModulePermissionApprovalRecord record, InstallAuditEntry entry, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ModulePermissionApprovalRecord>> ListPermissionApprovalsAsync(string moduleId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModulePermissionApprovalRecord>>(rows.Where(r => r.ModuleId == moduleId).ToList());
    }

    private sealed class Signatures(Func<ModulePackage, ModuleSignatureStatus>? decide = null) : IModuleSignatureVerifier
    {
        public ModuleSignatureStatus Verify(ModulePackage package) => decide?.Invoke(package) ?? ModuleSignatureStatus.Valid;
    }

    private sealed class Owner(bool accept = true) : IOwnerApprovalVerifier
    {
        public bool Verify(ReadOnlySpan<byte> approvalForm, OwnerApprovalSignature signature) => accept;
    }

    private sealed class NullSink : IModulePermissionDenialSink
    {
        public void Record(ModulePermissionDenial denial)
        {
        }
    }

    private static ComponentManifest Manifest(string id, string[]? dependsOn = null) =>
        new(id, "1.0.0", ComponentKind.Feature, dependsOn ?? [], []);

    private static ModulePackage Package(string id, params string[] declared) =>
        new(id, "1.0.0", _publisher, new HashSet<string>(declared, StringComparer.Ordinal), new string('a', 64), "key-1",
            new HostRequirements(1, ["linux"], ["x64"], []), [1]);

    private static ModulePermissionApprovalRecord Approval(string id, params string[] approved)
    {
        var form = ModuleApprovalForm.Build(id, "1.0.0", _publisher, approved);
        return new ModulePermissionApprovalRecord(id, "1.0.0", approved, Convert.ToHexStringLower(SHA256.HashData(form)), "owner-key", [1], "owner", _now);
    }

    private sealed record Rig(ModuleActivationHost Host, ModulePermissionGate Gate, ConfigurationManager Config);

    private static Rig Build(
        ComponentManifest[] manifests,
        IModulePackageSource packages,
        IModuleInstallStore store,
        IModuleSignatureVerifier? signatures = null,
        IOwnerApprovalVerifier? owner = null,
        Dictionary<string, string?>? config = null)
    {
        var gate = new ModulePermissionGate(new NullSink(), TimeProvider.System);
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(config ?? []);
        var host = new ModuleActivationHost(manifests, packages, store, signatures ?? new Signatures(), owner ?? new Owner(), gate, configuration, NullLogger<ModuleActivationHost>.Instance);
        return new Rig(host, gate, configuration);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModuleHealthReport Report(string id, ModuleHealthStatus status) =>
        new(id, [new ModuleHealthCheck(status, "test", _now)]);

    [Fact]
    public async Task An_approved_module_is_activated_and_the_gate_allows_what_it_declared()
    {
        var rig = Build([Manifest("weather")], new Packages(Package("weather", "data.read:gc", "gc.post")), new Store(Approval("weather", "data.read:gc", "gc.post")));

        var states = await rig.Host.ActivateAllAsync(Ct);

        Assert.Equal(ModuleActivationStatus.Active, Assert.Single(states).Status);
        Assert.Equal(ModuleAccessDecision.Allowed, rig.Gate.Check("weather", new ModuleAccess("data.read", "gc")));
        Assert.Equal(ModuleAccessDecision.DeniedNotGranted, rig.Gate.Check("weather", new ModuleAccess("data.write", "gc")));
    }

    [Fact]
    public async Task A_declared_permission_without_an_approval_refuses_the_start_and_nothing_is_in_force()
    {
        var rig = Build([Manifest("weather")], new Packages(Package("weather", "data.read:gc", "data.write:gc")), new Store(Approval("weather", "data.read:gc")));

        var state = Assert.Single(await rig.Host.ActivateAllAsync(Ct));

        Assert.Equal(ModuleActivationStatus.RefusedUnapproved, state.Status);
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, rig.Gate.Check("weather", new ModuleAccess("data.read", "gc")));
    }

    [Fact]
    public async Task An_approval_the_owner_key_does_not_verify_grants_nothing()
    {
        var rig = Build([Manifest("weather")], new Packages(Package("weather", "gc.post")), new Store(Approval("weather", "gc.post")), owner: new Owner(accept: false));

        Assert.Equal(ModuleActivationStatus.RefusedUnapproved, Assert.Single(await rig.Host.ActivateAllAsync(Ct)).Status);
    }

    [Fact]
    public async Task A_package_whose_signature_no_longer_verifies_does_not_start()
    {
        var rig = Build(
            [Manifest("weather")],
            new Packages(Package("weather", "gc.post")),
            new Store(Approval("weather", "gc.post")),
            new Signatures(_ => ModuleSignatureStatus.UnknownKey));

        var state = Assert.Single(await rig.Host.ActivateAllAsync(Ct));

        Assert.Equal(ModuleActivationStatus.RefusedSignature, state.Status);
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, rig.Gate.Check("weather", new ModuleAccess("gc.post")));
    }

    [Fact]
    public async Task A_module_with_no_installed_package_is_reported_not_packaged()
    {
        var rig = Build([Manifest("core-thing")], new Packages(), new Store());

        Assert.Equal(ModuleActivationStatus.NotPackaged, Assert.Single(await rig.Host.ActivateAllAsync(Ct)).Status);
    }

    [Fact]
    public async Task A_disabled_component_is_never_started()
    {
        var rig = Build(
            [Manifest("weather")],
            new Packages(Package("weather", "gc.post")),
            new Store(Approval("weather", "gc.post")),
            config: new() { ["Bazaar:Components:weather:Enabled"] = "false" });

        Assert.Equal(ModuleActivationStatus.Disabled, Assert.Single(await rig.Host.ActivateAllAsync(Ct)).Status);
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, rig.Gate.Check("weather", new ModuleAccess("gc.post")));
    }

    [Fact]
    public async Task A_module_whose_hard_dependency_was_refused_is_blocked_without_asking_the_guard()
    {
        var rig = Build(
            [Manifest("base"), Manifest("addon", ["base"])],
            new Packages(Package("base", "gc.post"), Package("addon", "gc.post")),
            new Store(Approval("addon", "gc.post")));

        var states = (await rig.Host.ActivateAllAsync(Ct)).ToDictionary(s => s.ModuleId);

        Assert.Equal(ModuleActivationStatus.RefusedUnapproved, states["base"].Status);
        Assert.Equal(ModuleActivationStatus.BlockedByDependency, states["addon"].Status);
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, rig.Gate.Check("addon", new ModuleAccess("gc.post")));
    }

    [Fact]
    public async Task A_failing_module_is_deactivated_with_its_hard_dependents_and_a_healthy_one_stays()
    {
        var rig = Build(
            [Manifest("base"), Manifest("addon", ["base"]), Manifest("other")],
            new Packages(Package("base", "gc.post"), Package("addon", "gc.post"), Package("other", "gc.post")),
            new Store(Approval("base", "gc.post"), Approval("addon", "gc.post"), Approval("other", "gc.post")));
        await rig.Host.ActivateAllAsync(Ct);

        var changed = await rig.Host.ApplyHealthAsync(
            [Report("base", ModuleHealthStatus.Failing), Report("addon", ModuleHealthStatus.Healthy), Report("other", ModuleHealthStatus.Healthy)], Ct);

        Assert.Equal(["addon", "base"], changed.Select(c => c.ModuleId));
        Assert.Equal(ModuleDeactivationReason.DependencyDeactivated, changed[0].Reason);
        Assert.Equal(ModuleDeactivationReason.HealthFailing, changed[1].Reason);
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, rig.Gate.Check("base", new ModuleAccess("gc.post")));
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, rig.Gate.Check("addon", new ModuleAccess("gc.post")));
        Assert.Equal(ModuleAccessDecision.Allowed, rig.Gate.Check("other", new ModuleAccess("gc.post")));
    }

    [Fact]
    public async Task A_degraded_module_keeps_its_set()
    {
        var rig = Build([Manifest("weather")], new Packages(Package("weather", "gc.post")), new Store(Approval("weather", "gc.post")));
        await rig.Host.ActivateAllAsync(Ct);

        Assert.Empty(await rig.Host.ApplyHealthAsync([Report("weather", ModuleHealthStatus.Degraded)], Ct));
        Assert.Equal(ModuleAccessDecision.Allowed, rig.Gate.Check("weather", new ModuleAccess("gc.post")));
    }

    [Fact]
    public async Task A_deactivated_module_starts_again_only_through_the_full_guard()
    {
        var verdict = ModuleSignatureStatus.Valid;
        var rig = Build([Manifest("weather")], new Packages(Package("weather", "gc.post")), new Store(Approval("weather", "gc.post")), new Signatures(_ => verdict));
        await rig.Host.ActivateAllAsync(Ct);
        await rig.Host.ApplyHealthAsync([Report("weather", ModuleHealthStatus.Failing)], Ct);

        // Health recovering does not bring it back.
        await rig.Host.ApplyHealthAsync([Report("weather", ModuleHealthStatus.Healthy)], Ct);
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, rig.Gate.Check("weather", new ModuleAccess("gc.post")));

        verdict = ModuleSignatureStatus.UnknownKey;
        Assert.Equal(ModuleActivationStatus.RefusedSignature, (await rig.Host.ActivateAsync("weather", Ct)).Status);
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, rig.Gate.Check("weather", new ModuleAccess("gc.post")));

        verdict = ModuleSignatureStatus.Valid;
        Assert.Equal(ModuleActivationStatus.Active, (await rig.Host.ActivateAsync("weather", Ct)).Status);
        Assert.Equal(ModuleAccessDecision.Allowed, rig.Gate.Check("weather", new ModuleAccess("gc.post")));
    }

    [Fact]
    public async Task Disabling_a_running_component_takes_its_set_away()
    {
        var rig = Build([Manifest("weather")], new Packages(Package("weather", "gc.post")), new Store(Approval("weather", "gc.post")));
        await rig.Host.ActivateAllAsync(Ct);

        rig.Config["Bazaar:Components:weather:Enabled"] = "false";
        var changed = await rig.Host.ApplyEnablementAsync(Ct);

        Assert.Equal(ModuleDeactivationReason.Disabled, Assert.Single(changed).Reason);
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, rig.Gate.Check("weather", new ModuleAccess("gc.post")));
    }

    [Fact]
    public async Task Stopping_the_host_takes_every_set_away()
    {
        var rig = Build([Manifest("weather")], new Packages(Package("weather", "gc.post")), new Store(Approval("weather", "gc.post")));
        await rig.Host.ActivateAllAsync(Ct);

        await rig.Host.DeactivateAllAsync(Ct);

        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, rig.Gate.Check("weather", new ModuleAccess("gc.post")));
    }

    [Fact]
    public async Task An_unknown_module_id_is_an_argument_error()
    {
        var rig = Build([Manifest("weather")], new Packages(), new Store());

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Host.DeactivateAsync("nope", ModuleDeactivationReason.Requested, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Host.ActivateAsync("nope", Ct));
    }
}
