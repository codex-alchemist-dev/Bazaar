using Bazaar.Server.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Garnet.Tests;

/// <summary>
/// Runs the real Garnet server through the module (spike S2 follow-up). Only on Windows and only when <c>BAZAAR_GARNET_BINARY</c> names the
/// server program, so a normal test run skips it. The <c>garnet-windows</c> workflow installs the released tool and sets the variable.
/// </summary>
public sealed class RealGarnetBinaryTests
{
    private const string BinaryVariable = "BAZAAR_GARNET_BINARY";

    [Fact]
    public async Task The_managed_server_starts_answers_and_keeps_data_across_a_restart()
    {
        var binary = Environment.GetEnvironmentVariable(BinaryVariable);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(binary))
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var data = Path.Combine(Path.GetTempPath(), "bazaar-garnet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        try
        {
            var key = "real:key";
            byte[] value = [1, 2, 3, 4];

            await using (var first = Host(binary, data))
            {
                var supervisor = first.GetRequiredService<GarnetServerSupervisor>();
                await supervisor.StartAsync(ct);
                try
                {
                    var cache = first.GetServices<ICacheProvider>().OfType<GarnetCacheProvider>().Single();
                    var status = cache.Probe();
                    Assert.True(status.Usable, status.Reason);

                    await cache.SetAsync(key, value, TimeSpan.FromMinutes(5), ct);
                    Assert.Equal(value, (await cache.GetAsync(key, ct))!.Value.ToArray());
                }
                finally
                {
                    await supervisor.StopAsync(CancellationToken.None);
                }
            }

            // A second host on the same data directory recovers the append-only file.
            await using var second = Host(binary, data);
            var again = second.GetRequiredService<GarnetServerSupervisor>();
            await again.StartAsync(ct);
            try
            {
                var cache = second.GetServices<ICacheProvider>().OfType<GarnetCacheProvider>().Single();
                Assert.True(cache.Probe().Usable);
                Assert.Equal(value, (await cache.GetAsync(key, ct))?.ToArray());
            }
            finally
            {
                await again.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (IOException)
            {
                // The server may still hold a file for a moment; the runner is discarded anyway.
            }
        }
    }

    private static ServiceProvider Host(string binary, string data)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Cache:Garnet:Managed"] = "true",
            ["Bazaar:Cache:Garnet:BinaryPath"] = binary,
            ["Bazaar:Cache:Garnet:DataDirectory"] = data,
            ["Bazaar:Cache:Garnet:StartWindowSeconds"] = "60",
            ["Bazaar:Cache:Garnet:Supervision:StartupTimeout"] = "00:00:45",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        services.AddBazaarGarnet(configuration);
        return services.BuildServiceProvider();
    }
}
