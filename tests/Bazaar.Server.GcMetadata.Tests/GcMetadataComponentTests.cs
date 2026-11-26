using Bazaar.Server.GcMetadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.GcMetadata.Tests;

public class GcMetadataComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    [Fact]
    public void Manifest_names_the_gc_and_audit_components()
    {
        var manifest = GcMetadataComponent.ManifestDefinition;

        Assert.Equal("gc-metadata", manifest.Id);
        Assert.Equal(["gc", "audit"], manifest.DependsOn);
    }

    [Fact]
    public async Task Options_default_to_owner_only_with_the_marker_on()
    {
        var services = new ServiceCollection();
        new GcMetadataComponent().AddServices(services, Config());
        await using var sp = services.BuildServiceProvider();

        var options = await sp.GetRequiredService<IDmAccessOptionsSource>().GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DmApprovers.Owner, options.Approvers);
        Assert.True(options.ShowOwnerReadMarker);
        Assert.Equal(GcMetadataSchema.Default.AllowedStatuses, sp.GetRequiredService<GcMetadataSchema>().AllowedStatuses);
    }

    [Fact]
    public async Task Options_bind_from_configuration()
    {
        var services = new ServiceCollection();
        new GcMetadataComponent().AddServices(services, Config(
            ("Bazaar:GcMetadata:Approvers", "owner-or-dm-member"),
            ("Bazaar:GcMetadata:ShowOwnerReadMarker", "false"),
            ("Bazaar:GcMetadata:Statuses:0", "draft"),
            ("Bazaar:GcMetadata:Statuses:1", "shipped")));
        await using var sp = services.BuildServiceProvider();

        var options = await sp.GetRequiredService<IDmAccessOptionsSource>().GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DmApprovers.OwnerOrDmMember, options.Approvers);
        Assert.False(options.ShowOwnerReadMarker);
        Assert.Equal(["draft", "shipped"], sp.GetRequiredService<GcMetadataSchema>().AllowedStatuses);
    }

    [Fact]
    public void A_bad_approvers_value_fails_at_start_so_a_typo_never_loosens_a_rule()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            new GcMetadataComponent().AddServices(services, Config(("Bazaar:GcMetadata:Approvers", "everyone"))));
    }

    [Fact]
    public void A_host_registered_options_source_is_kept()
    {
        var services = new ServiceCollection();
        var mine = new StaticDmAccessOptionsSource(new DmAccessOptions(DmApprovers.OwnerOrDmMember));
        services.AddSingleton<IDmAccessOptionsSource>(mine);
        new GcMetadataComponent().AddServices(services, Config());
        using var sp = services.BuildServiceProvider();

        Assert.Same(mine, sp.GetRequiredService<IDmAccessOptionsSource>());
    }
}
