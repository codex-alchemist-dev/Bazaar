using Bazaar.Contracts.Wire;
using Bazaar.Server.Constellation.Memory;
using Bazaar.Server.Constellation.Memory.Context;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

/// <summary>
/// Host wiring of the memory context retrieval operation (docs/design/constellation-memory-graph.md, "Retrieval operation"): the full
/// host graph resolves the service, the <c>memory.context</c> wire handler and the <c>memory.context.*</c> settings.
/// </summary>
public sealed class MemoryContextCompositionTests
{
    [Fact]
    public void The_full_host_graph_resolves_the_memory_context_service_its_wire_command_and_its_settings()
    {
        const string dbEnv = "BAZAAR_MEMCTX_TEST_DB";
        const string tokenEnv = "BAZAAR_MEMCTX_TEST_TOKEN";
        Environment.SetEnvironmentVariable(dbEnv, "Host=localhost;Database=unused");
        Environment.SetEnvironmentVariable(tokenEnv, "dev-secret-for-memory-context-test");
        try
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:Host:DataRoot"] = Path.Combine(Path.GetTempPath(), "bazaar-memctx-test-" + Guid.NewGuid().ToString("N")),
                ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + dbEnv,
                ["Bazaar:DevAuth:Enabled"] = "true",
                ["Bazaar:DevAuth:TokenSecret"] = "env:" + tokenEnv,
            });
            builder.Services.AddBazaarHostApplication(builder.Configuration);
            using var host = builder.Build();
            var sp = host.Services;

            Assert.IsType<MemoryContextService>(sp.GetRequiredService<IMemoryContextService>());
            Assert.Single(sp.GetServices<IWireRequestHandler>(), h => h.Type == WireTypes.MemoryContext);
            Assert.IsType<SettingStoreMemoryContextLimits>(sp.GetRequiredService<IMemoryContextLimitsSource>());
            Assert.NotNull(sp.GetRequiredService<MemoryGraphRetriever>());

            var registry = sp.GetRequiredService<SettingRegistry>();
            Assert.All(MemoryContextSettings.All, d => Assert.True(registry.Contains(d), d.Name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(dbEnv, null);
            Environment.SetEnvironmentVariable(tokenEnv, null);
        }
    }

    [Fact]
    public void Hard_caps_refuse_a_setting_above_them_and_clamp_fixed_limits()
    {
        Assert.Null(MemoryContextSettings.MaxTokensKey.CheckValue(Json(8000)));
        Assert.NotNull(MemoryContextSettings.MaxTokensKey.CheckValue(Json(8001)));
        Assert.NotNull(MemoryContextSettings.MaxHitsKey.CheckValue(Json(51)));
        Assert.NotNull(MemoryContextSettings.RatePerAgentPerMinuteKey.CheckValue(Json(0)));

        var clamped = new MemoryContextLimits(100_000, 1000, 100_000, 100_000).Clamped();
        Assert.Equal(new MemoryContextLimits(8000, 50, 8000, 600), clamped);
        Assert.Equal(new MemoryContextLimits(2000, 12, 2000, 30), MemoryContextLimits.Defaults);
    }

    private static System.Text.Json.JsonElement Json(int value) => System.Text.Json.JsonSerializer.SerializeToElement(value);
}
