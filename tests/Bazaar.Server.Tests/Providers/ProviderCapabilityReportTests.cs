using Bazaar.Server.Providers;

namespace Bazaar.Server.Tests.Providers;

public class ProviderCapabilityReportTests
{
    [Fact]
    public void Lines_describe_each_selection_in_order()
    {
        var search = ProviderSelector.Select(ProviderRole.Search, "auto", [new Usable("postgres_fts")]);
        var vm = ProviderSelector.Select(ProviderRole.Vm, "none", [new Usable("hyperv")]);

        var report = new ProviderCapabilityReport([search, vm]);

        Assert.Equal(
            ["provider search: postgres_fts selected (requested auto)", "provider vm: none (disabled by setting)"],
            report.Lines);
    }

    [Fact]
    public void Lines_name_unusable_candidates_with_their_reasons()
    {
        var cache = ProviderSelector.Select<IProvider>(
            ProviderRole.Cache,
            "auto",
            [new Unusable("garnet", "spike S2 pending"), new Usable("valkey")]);

        var line = new ProviderCapabilityReport([cache]).Lines.Single();

        Assert.Equal("provider cache: valkey selected (requested auto); unusable: garnet (spike S2 pending)", line);
    }

    [Fact]
    public void Auto_with_nothing_usable_reports_no_usable_provider()
    {
        var service = ProviderSelector.Select(ProviderRole.ServiceHost, "auto", [new Unusable("systemd", "not running under systemd")]);

        Assert.Equal(
            "provider service_host: no usable provider (requested auto); unusable: systemd (not running under systemd)",
            new ProviderCapabilityReport([service]).Lines.Single());
    }

    [Fact]
    public void Missing_required_roles_lists_roles_without_a_provider()
    {
        var report = new ProviderCapabilityReport(
        [
            ProviderSelector.Select(ProviderRole.Search, "auto", [new Usable("postgres_fts")]),
            ProviderSelector.Select(ProviderRole.ServiceHost, "auto", [new Unusable("systemd", "no")]),
            ProviderSelector.Select(ProviderRole.Vm, "none", [new Usable("hyperv")]),
        ]);

        var missing = report.MissingRequiredRoles([ProviderRole.Search, ProviderRole.ServiceHost, ProviderRole.Vm]);

        Assert.Equal([ProviderRole.ServiceHost, ProviderRole.Vm], missing);
    }

    [Fact]
    public void Required_role_absent_from_the_report_is_missing()
    {
        var report = new ProviderCapabilityReport([]);

        Assert.Equal([ProviderRole.Search], report.MissingRequiredRoles([ProviderRole.Search, ProviderRole.Search]));
    }

    [Fact]
    public void No_required_roles_means_nothing_is_missing()
    {
        var report = new ProviderCapabilityReport([]);

        Assert.Empty(report.MissingRequiredRoles([]));
    }

    private sealed class Usable(string kind) : IProvider
    {
        public string Kind { get; } = kind;

        public ProviderStatus Probe() => new(Kind, true, [], null);
    }

    private sealed class Unusable(string kind, string reason) : IProvider
    {
        public string Kind { get; } = kind;

        public ProviderStatus Probe() => new(Kind, false, [], reason);
    }
}
