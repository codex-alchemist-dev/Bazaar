using Bazaar.Server.Providers;

namespace Bazaar.Server.Tests.Providers;

/// <summary>A role can start pending and be resolved later, and the report stays consistent when read while it is resolved.</summary>
public class ProviderCapabilityPendingTests
{
    private sealed class FakeProvider(string kind) : IProvider
    {
        public string Kind => kind;

        public ProviderStatus Probe() => new(kind, true, [], null);
    }

    [Fact]
    public void A_pending_role_logs_as_pending_and_is_not_probed()
    {
        var report = new ProviderCapabilityReport([ProviderSelection.Pending(ProviderRole.Cache, "auto")]);

        var selection = Assert.Single(report.Selections);
        Assert.True(selection.IsPending);
        Assert.Null(selection.Provider);
        Assert.Equal("provider cache: pending (requested auto)", Assert.Single(report.Lines));
    }

    [Fact]
    public void A_pending_role_is_not_reported_as_missing_because_the_host_has_not_decided_it()
    {
        var report = new ProviderCapabilityReport([ProviderSelection.Pending(ProviderRole.Cache, "auto")]);

        Assert.Empty(report.MissingRequiredRoles([ProviderRole.Cache]));
    }

    [Fact]
    public void Resolve_replaces_the_pending_selection_of_the_same_role()
    {
        var report = new ProviderCapabilityReport([ProviderSelection.Pending(ProviderRole.Cache, "auto")]);

        report.Resolve(new ProviderSelection(ProviderRole.Cache, "auto", new FakeProvider("valkey"), []));

        var selection = Assert.Single(report.Selections);
        Assert.False(selection.IsPending);
        Assert.Equal("valkey", selection.Provider!.Kind);
        Assert.Equal("provider cache: valkey selected (requested auto)", Assert.Single(report.Lines));
        Assert.Empty(report.MissingRequiredRoles([ProviderRole.Cache]));
    }

    [Fact]
    public void Resolve_adds_a_role_that_had_no_selection()
    {
        var report = new ProviderCapabilityReport([]);

        report.Resolve(ProviderSelector.Select(ProviderRole.Search, "none", Array.Empty<ISearchProvider>()));

        Assert.Single(report.Selections);
    }

    [Fact]
    public void A_resolved_role_that_has_no_provider_is_missing_when_it_is_required()
    {
        var report = new ProviderCapabilityReport([ProviderSelection.Pending(ProviderRole.Cache, "auto")]);

        report.Resolve(new ProviderSelection(ProviderRole.Cache, "auto", null, []));

        Assert.Equal([ProviderRole.Cache], report.MissingRequiredRoles([ProviderRole.Cache]));
    }

    [Fact]
    public async Task Reads_and_resolves_can_interleave_from_several_threads()
    {
        var report = new ProviderCapabilityReport([ProviderSelection.Pending(ProviderRole.Cache, "auto")]);

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                report.Resolve(new ProviderSelection(ProviderRole.Cache, "auto", new FakeProvider("valkey"), []));
            }
        }, TestContext.Current.CancellationToken);
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                _ = report.Lines;
                _ = report.MissingRequiredRoles([ProviderRole.Cache]);
            }
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(writer, reader);

        Assert.Single(report.Selections);
    }
}
