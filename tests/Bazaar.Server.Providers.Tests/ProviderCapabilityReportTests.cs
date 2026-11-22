using Bazaar.Server.Providers.Fakes;

namespace Bazaar.Server.Providers.Tests;

public class ProviderCapabilityReportTests
{
    [Fact]
    public void No_providers_gives_an_empty_report_and_every_required_role_is_missing()
    {
        var report = new ProviderCapabilityReport([]);

        Assert.Empty(report.Entries);
        Assert.Empty(report.Lines);
        Assert.Equal([ProviderRole.Search, ProviderRole.Cache], report.MissingRequiredRoles([ProviderRole.Search, ProviderRole.Cache, ProviderRole.Search]));
        Assert.Empty(report.MissingRequiredRoles([]));
    }

    [Fact]
    public void One_provider_is_listed_with_its_own_status()
    {
        var report = new ProviderCapabilityReport([new FakeSearchProvider("postgres_fts")]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(ProviderRole.Search, entry.Role);
        Assert.Equal("postgres_fts", entry.Kind);
        Assert.True(entry.Status.Usable);
        Assert.Equal("provider search: postgres_fts usable", Assert.Single(report.Lines));
        Assert.Empty(report.MissingRequiredRoles([ProviderRole.Search]));
    }

    [Fact]
    public void Several_providers_of_one_role_are_all_listed_in_registration_order()
    {
        var report = new ProviderCapabilityReport(
        [
            new FakeSearchProvider("pg_search", usable: false),
            new FakeSearchProvider("postgres_fts"),
            new FakeSearchProvider("other"),
        ]);

        Assert.Equal(
            [
                "provider search: pg_search unusable (fake provider unavailable)",
                "provider search: postgres_fts usable",
                "provider search: other usable",
            ],
            report.Lines);
        Assert.Equal(3, report.Entries.Count(e => e.Role == ProviderRole.Search));
    }

    [Fact]
    public void A_role_is_satisfied_by_one_usable_provider_among_several()
    {
        var report = new ProviderCapabilityReport([new FakeSearchProvider("a", usable: false), new FakeSearchProvider("b")]);

        Assert.Empty(report.MissingRequiredRoles([ProviderRole.Search]));
    }

    [Fact]
    public void A_role_is_missing_when_every_provider_of_it_is_unusable()
    {
        var report = new ProviderCapabilityReport([new FakeSearchProvider("a", usable: false), new FakeSearchProvider("b", usable: false)]);

        Assert.Equal([ProviderRole.Search], report.MissingRequiredRoles([ProviderRole.Search]));
    }

    [Fact]
    public void Roles_are_told_apart_and_only_the_required_ones_are_checked()
    {
        var report = new ProviderCapabilityReport([new FakeSearchProvider("postgres_fts"), new FakeCacheProvider()]);

        Assert.Equal([ProviderRole.Vm], report.MissingRequiredRoles([ProviderRole.Search, ProviderRole.Cache, ProviderRole.Vm]));
    }

    [Fact]
    public void A_provider_without_a_role_interface_is_listed_without_a_role()
    {
        var report = new ProviderCapabilityReport([new Plain("odd")]);

        var entry = Assert.Single(report.Entries);
        Assert.Null(entry.Role);
        Assert.Equal("provider: odd usable", Assert.Single(report.Lines));
    }

    [Fact]
    public void A_probe_that_throws_is_reported_unusable_without_the_message()
    {
        var report = new ProviderCapabilityReport([new Throwing()]);

        var entry = Assert.Single(report.Entries);
        Assert.False(entry.Status.Usable);
        Assert.Equal("probe failed (InvalidOperationException)", entry.Status.Reason);
        Assert.DoesNotContain("secret", entry.ToLogLine());
    }

    [Fact]
    public void Status_is_read_live_on_every_call()
    {
        var provider = new Toggle();
        var report = new ProviderCapabilityReport([provider]);

        Assert.Equal("provider: toggle unusable (starting)", Assert.Single(report.Lines));

        provider.Usable = true;

        Assert.Equal("provider: toggle usable", Assert.Single(report.Lines));
    }

    private sealed class Plain(string kind) : IProvider
    {
        public string Kind { get; } = kind;

        public ProviderStatus Probe() => new(Kind, true, [], null);
    }

    private sealed class Throwing : IProvider
    {
        public string Kind => "boom";

        public ProviderStatus Probe() => throw new InvalidOperationException("secret");
    }

    private sealed class Toggle : IProvider
    {
        public bool Usable { get; set; }

        public string Kind => "toggle";

        public ProviderStatus Probe() => new(Kind, Usable, [], Usable ? null : "starting");
    }
}
