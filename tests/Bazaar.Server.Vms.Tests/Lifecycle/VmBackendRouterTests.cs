using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.Tests.Lifecycle;

/// <summary>A source with a swappable backend and a usable flag, for routing tests.</summary>
internal sealed class FakeSource(string kind, VmProviderPlacement placement, int preference, params VmOs[] os) : IVmBackendSource
{
    public string Kind { get; } = kind;

    public IReadOnlyList<VmOs> GuestOs { get; } = os;

    public VmProviderPlacement Placement { get; } = placement;

    public int Preference { get; } = preference;

    public FakeVmBackend? Backend { get; set; } = new();

    public bool Usable { get; set; } = true;

    public int Lookups { get; private set; }

    public ValueTask<VmBackendLookup> GetCurrentAsync(CancellationToken ct)
    {
        Lookups++;
        return ValueTask.FromResult(new VmBackendLookup(Backend, Usable && Backend is not null, Usable ? null : $"{Kind} probe failed"));
    }
}

internal sealed class FakeGate(params string[] enabled) : IVmProviderGate
{
    public HashSet<string> Enabled { get; } = new(enabled, StringComparer.Ordinal);

    public ValueTask<bool> IsEnabledAsync(string kind, CancellationToken ct) => ValueTask.FromResult(Enabled.Contains(kind));
}

/// <summary>The routing rules of doc 62 section 9.2.</summary>
public sealed class VmBackendRouterTests
{
    private static readonly DateTimeOffset _epoch = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeSource Pair(string kind = "hyperv", int preference = VmBackendOrdering.PrimaryHypervisor) =>
        new(kind, VmProviderPlacement.Pair, preference, VmOs.Windows, VmOs.Linux);

    private static FakeSource Android(string kind = "android") => new(kind, VmProviderPlacement.OnDemand, VmBackendOrdering.OnDemand, VmOs.Android);

    private static FakeSource MicroVm() => new("microvm", VmProviderPlacement.OnDemand, VmBackendOrdering.OnDemand, VmOs.Linux);

    private static EnvironmentVmRecord Row(VmOs os, string? kind) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), os, Guid.CreateVersion7(), "ref-1", VmState.Stopped, 2, 2048, 30, _epoch, _epoch, kind);

    [Fact]
    public async Task A_windows_or_linux_vm_without_a_kind_goes_to_the_selected_pair_provider()
    {
        var pair = Pair();
        var router = new VmBackendRouter([pair, MicroVm()], new VmPairSelection("hyperv"), new FakeGate("microvm"));

        var route = await router.RouteNewAsync(VmOs.Linux, null, Ct);

        Assert.Equal("hyperv", route.Kind);
        Assert.Same(pair.Backend, route.Backend);
    }

    [Fact]
    public async Task Without_a_pair_provider_a_linux_vm_is_refused_and_an_enabled_microvm_is_not_used_silently()
    {
        var micro = MicroVm();
        var router = new VmBackendRouter([micro], VmPairSelection.None, new FakeGate("microvm"));

        var route = await router.RouteNewAsync(VmOs.Linux, null, Ct);

        Assert.False(route.Found);
        Assert.Contains("host.provider.vm", route.Reason, StringComparison.Ordinal);
        Assert.Equal(0, micro.Lookups);
    }

    [Fact]
    public async Task A_named_on_demand_kind_needs_to_be_enabled()
    {
        var micro = MicroVm();
        var gate = new FakeGate();
        var router = new VmBackendRouter([Pair(), micro], new VmPairSelection("hyperv"), gate);

        var refused = await router.RouteNewAsync(VmOs.Linux, "microvm", Ct);
        Assert.False(refused.Found);
        Assert.Contains("vms.devices.enabled", refused.Reason, StringComparison.Ordinal);

        gate.Enabled.Add("microvm");
        var routed = await router.RouteNewAsync(VmOs.Linux, "microvm", Ct);
        Assert.Equal("microvm", routed.Kind);
        Assert.Same(micro.Backend, routed.Backend);
    }

    [Fact]
    public async Task A_named_kind_that_is_missing_or_runs_another_os_is_refused()
    {
        var router = new VmBackendRouter([Pair(), Android()], new VmPairSelection("hyperv"), new FakeGate("android"));

        var missing = await router.RouteNewAsync(VmOs.Linux, "kvm", Ct);
        var wrongOs = await router.RouteNewAsync(VmOs.Linux, "android", Ct);

        Assert.Contains("'kvm' is installed", missing.Reason, StringComparison.Ordinal);
        Assert.Contains("does not run linux guests", wrongOs.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_android_vm_goes_to_the_first_enabled_provider_and_is_refused_when_none_is_enabled()
    {
        var android = Android();
        var gate = new FakeGate();
        var router = new VmBackendRouter([Pair(), android], new VmPairSelection("hyperv"), gate);

        Assert.Contains("vms.devices.enabled", (await router.RouteNewAsync(VmOs.Android, null, Ct)).Reason, StringComparison.Ordinal);

        gate.Enabled.Add("android");
        var route = await router.RouteNewAsync(VmOs.Android, null, Ct);
        Assert.Equal("android", route.Kind);
    }

    [Fact]
    public async Task An_unusable_first_choice_is_refused_with_its_reason_and_the_next_one_is_not_tried()
    {
        var first = Android("android");
        first.Usable = false;
        var second = Android("android_alt");
        var router = new VmBackendRouter([second, first], VmPairSelection.None, new FakeGate("android", "android_alt"));

        var route = await router.RouteNewAsync(VmOs.Android, null, Ct);

        Assert.False(route.Found);
        Assert.Contains("android probe failed", route.Reason, StringComparison.Ordinal);
        Assert.Equal(0, second.Lookups);
    }

    [Fact]
    public void Sources_are_ordered_by_preference_then_kind()
    {
        var router = new VmBackendRouter(
            [Pair("windows_sandbox", VmBackendOrdering.LastResort), Android("b_kind"), Android("a_kind"), Pair("hyperv")],
            VmPairSelection.None,
            gate: null);

        Assert.Equal(["hyperv", "a_kind", "b_kind", "windows_sandbox"], router.Sources.Select(s => s.Kind));
    }

    [Fact]
    public void Two_sources_with_one_kind_are_a_registration_error()
    {
        Assert.Throws<ArgumentException>(() => new VmBackendRouter([Android(), Android()], VmPairSelection.None, gate: null));
    }

    [Fact]
    public async Task Without_a_gate_no_on_demand_kind_is_enabled()
    {
        var router = new VmBackendRouter([Android()], VmPairSelection.None, gate: null);

        Assert.False((await router.RouteNewAsync(VmOs.Android, "android", Ct)).Found);
    }

    [Fact]
    public async Task An_existing_vm_routes_by_its_kind_even_when_the_provider_is_disabled_or_its_probe_fails()
    {
        var android = Android();
        android.Usable = false;
        var router = new VmBackendRouter([android], VmPairSelection.None, new FakeGate());

        var route = await router.RouteExistingAsync(Row(VmOs.Android, "android"), Ct);

        Assert.Same(android.Backend, route.Backend);
    }

    [Fact]
    public async Task An_existing_vm_whose_provider_is_not_installed_is_refused()
    {
        var router = new VmBackendRouter([Pair()], new VmPairSelection("hyperv"), gate: null);

        var route = await router.RouteExistingAsync(Row(VmOs.Android, "android"), Ct);

        Assert.False(route.Found);
        Assert.Contains("'android' is not installed", route.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_legacy_row_routes_to_the_pair_provider_or_else_to_the_single_provider_for_its_os()
    {
        var sandbox = new FakeSource("windows_sandbox", VmProviderPlacement.Pair, VmBackendOrdering.LastResort, VmOs.Windows);
        var unselected = new VmBackendRouter([sandbox], VmPairSelection.None, gate: null);
        Assert.Same(sandbox.Backend, (await unselected.RouteExistingAsync(Row(VmOs.Windows, null), Ct)).Backend);

        var hyperV = Pair();
        var selected = new VmBackendRouter([sandbox, hyperV], new VmPairSelection("hyperv"), gate: null);
        Assert.Same(hyperV.Backend, (await selected.RouteExistingAsync(Row(VmOs.Windows, null), Ct)).Backend);

        var ambiguous = new VmBackendRouter([sandbox, hyperV], VmPairSelection.None, gate: null);
        Assert.False((await ambiguous.RouteExistingAsync(Row(VmOs.Windows, null), Ct)).Found);
    }

    [Fact]
    public async Task A_vm_stays_on_its_pinned_backend_when_the_source_hands_out_a_new_one_until_it_is_released()
    {
        var android = Android();
        var router = new VmBackendRouter([android], VmPairSelection.None, new FakeGate("android"));
        var row = Row(VmOs.Android, "android");
        var original = android.Backend!;
        router.Pin(row.Id, "android", original);

        android.Backend = new FakeVmBackend();
        Assert.Same(original, (await router.RouteExistingAsync(row, Ct)).Backend);

        router.Release(row.Id);
        Assert.Same(android.Backend, (await router.RouteExistingAsync(row, Ct)).Backend);
    }
}
