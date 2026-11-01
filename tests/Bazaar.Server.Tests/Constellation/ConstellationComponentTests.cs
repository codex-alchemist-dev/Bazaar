using Bazaar.Contracts.Graph;
using Bazaar.Server.Components;
using Bazaar.Server.Constellation;
using Bazaar.Server.Constellation.Tiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Constellation;

/// <summary>The Constellation component's manifest and registration (doc 19, BAZAAR-COMPONENTS rule 5). No database.</summary>
public sealed class ConstellationComponentTests
{
    private static ViewportRequest AnyViewport() =>
        new("view-1", 7, ZoomTier.Far, -10f, -10f, 0f, 10f, 10f, 0f, 100);

    [Fact]
    public void Constellation_manifest_is_a_feature_with_no_dependencies()
    {
        var manifest = new ConstellationComponent().Manifest;

        Assert.Equal("constellation", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Contains("feature:constellation", manifest.Provides);
    }

    [Fact]
    public async Task Component_registers_a_tile_handler_that_answers_not_found_without_a_layout_store()
    {
        var services = new ServiceCollection();

        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new ConstellationComponent()]);

        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<ConstellationTileHandler>();
        TileResponse response = await handler.HandleAsync(AnyViewport(), TestContext.Current.CancellationToken);
        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task A_tile_source_registered_before_the_component_wins_over_the_default()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConstellationTileSource>(
            new FakeConstellationTileSource(7, [new FakeTileNode("a", 0f, 0f, 0f)]));

        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new ConstellationComponent()]);

        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<ConstellationTileHandler>();
        TileResponse response = await handler.HandleAsync(AnyViewport(), TestContext.Current.CancellationToken);
        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public void A_disabled_component_registers_nothing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Components:constellation:Enabled"] = "false" })
            .Build();
        var services = new ServiceCollection();

        services.AddBazaarComponents(configuration, [new ConstellationComponent()]);

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<ConstellationTileHandler>());
        Assert.Null(provider.GetService<IConstellationTileSource>());
    }
}
