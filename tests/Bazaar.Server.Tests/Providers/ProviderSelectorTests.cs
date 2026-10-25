using Bazaar.Server.Providers;

namespace Bazaar.Server.Tests.Providers;

public class ProviderSelectorTests
{
    [Fact]
    public void Auto_picks_the_first_usable_candidate_in_list_order()
    {
        var unusable = new StubProvider("pg_search", usable: false, reason: "extension missing");
        var fallback = new StubProvider("postgres_fts", usable: true);
        var selection = ProviderSelector.Select(ProviderRole.Search, "auto", [unusable, fallback]);

        Assert.Same(fallback, selection.Provider);
        Assert.Equal(2, selection.Statuses.Count);
    }

    [Fact]
    public void Auto_with_no_usable_candidate_selects_nothing_and_keeps_the_probe_results()
    {
        var selection = ProviderSelector.Select(
            ProviderRole.Cache,
            "auto",
            [new StubProvider("garnet", usable: false, reason: "spike S2 pending"), new StubProvider("valkey", usable: false)]);

        Assert.Null(selection.Provider);
        Assert.Equal(["garnet", "valkey"], selection.Statuses.Select(s => s.Kind));
    }

    [Fact]
    public void Auto_with_no_candidates_selects_nothing()
    {
        var selection = ProviderSelector.Select<StubProvider>(ProviderRole.Vm, "auto", []);

        Assert.Null(selection.Provider);
        Assert.Empty(selection.Statuses);
    }

    [Fact]
    public void Explicit_kind_is_chosen_even_when_it_is_not_first()
    {
        var first = new StubProvider("hyperv", usable: true);
        var second = new StubProvider("qemu_kvm", usable: true);
        var selection = ProviderSelector.Select(ProviderRole.Vm, "qemu_kvm", [first, second]);

        Assert.Same(second, selection.Provider);
    }

    [Fact]
    public void Explicit_kind_matches_case_insensitively()
    {
        var provider = new StubProvider("systemd", usable: true);
        var selection = ProviderSelector.Select(ProviderRole.ServiceHost, "SystemD", [provider]);

        Assert.Same(provider, selection.Provider);
    }

    [Fact]
    public void Explicit_unusable_kind_fails_and_names_the_reason()
    {
        var candidates = new[] { new StubProvider("garnet", usable: false, reason: "spike S2 pending") };

        var error = Assert.Throws<ProviderSelectionException>(() => ProviderSelector.Select(ProviderRole.Cache, "garnet", candidates));

        Assert.Equal(ProviderRole.Cache, error.Role);
        Assert.Contains("spike S2 pending", error.Message);
    }

    [Fact]
    public void Explicit_kind_not_registered_for_this_os_fails()
    {
        var linuxOnly = new[] { new StubProvider("valkey", usable: true) };

        var error = Assert.Throws<ProviderSelectionException>(() => ProviderSelector.Select(ProviderRole.Cache, "garnet", linuxOnly));

        Assert.Equal("garnet", error.Requested);
    }

    [Fact]
    public void None_disables_the_role_without_a_provider()
    {
        var selection = ProviderSelector.Select(ProviderRole.Vm, "none", [new StubProvider("hyperv", usable: true)]);

        Assert.Null(selection.Provider);
        Assert.Empty(selection.Statuses);
        Assert.Equal("provider vm: none (disabled by setting)", selection.ToLogLine());
    }

    [Fact]
    public void Blank_requested_value_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ProviderSelector.Select(ProviderRole.Search, " ", [new StubProvider("postgres_fts", usable: true)]));
    }

    [Theory]
    [InlineData("windows", "pg_search", false, "postgres_fts")]
    [InlineData("linux", "pg_search", true, "pg_search")]
    public void Search_auto_picks_the_per_os_provider(string os, string preferredKind, bool preferredUsable, string expectedKind)
    {
        // Mirrors the doc 03 matrix: pg_search is preferred when present, Postgres FTS is the always-usable fallback.
        var candidates = new[]
        {
            new StubProvider(preferredKind, usable: preferredUsable, reason: $"{os}: not installed"),
            new StubProvider("postgres_fts", usable: true),
        };

        var selection = ProviderSelector.Select(ProviderRole.Search, "auto", candidates);

        Assert.Equal(expectedKind, selection.Provider?.Kind);
    }

    /// <summary>Probe result under test control. Not a real provider.</summary>
    private sealed class StubProvider(string kind, bool usable, string? reason = null) : IProvider
    {
        public string Kind { get; } = kind;

        public ProviderStatus Probe() => new(Kind, usable, [], usable ? null : reason ?? "not usable");
    }
}
