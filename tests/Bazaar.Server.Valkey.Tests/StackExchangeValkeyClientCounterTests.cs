using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Bazaar.Server.Valkey.Tests;

/// <summary>
/// Runs <see cref="StackExchangeValkeyClient.IncrementAsync"/> against a real server. Skipped when no <c>valkey-server</c> or
/// <c>redis-server</c> (7.0 or newer, for <c>EXPIRE NX</c>) is on the PATH.
/// </summary>
public sealed class StackExchangeValkeyClientCounterTests
{
    [Fact]
    public async Task Increment_is_atomic_sets_the_expiry_only_on_a_new_key_and_never_leaves_a_key_without_one()
    {
        var binary = FindServer();
        Assert.SkipUnless(binary is not null, "No valkey-server or redis-server on the PATH.");
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        using var server = Process.Start(new ProcessStartInfo(binary!, ["--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--save", "", "--appendonly", "no", "--bind", "127.0.0.1"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            await using var client = new StackExchangeValkeyClient($"127.0.0.1:{port},connectTimeout=5000");
            await WaitReadyAsync(client, ct);

            Assert.Equal(1, await client.IncrementAsync("c", 1, TimeSpan.FromSeconds(100), ct));
            var ttl1 = await ReadTtlAsync(port);
            Assert.InRange(ttl1, 90, 100);

            // A later call with a much longer ttl does not extend the existing expiry.
            Assert.Equal(3, await client.IncrementAsync("c", 2, TimeSpan.FromSeconds(10_000), ct));
            Assert.InRange(await ReadTtlAsync(port), 1, 100);

            Assert.Equal(2, await client.IncrementAsync("c", -1, TimeSpan.FromSeconds(10_000), ct));
            Assert.Equal(0, await client.IncrementAsync("z", 0, TimeSpan.FromSeconds(50), ct));

            // Parallel increments never lose a count.
            await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => client.IncrementAsync("p", 1, TimeSpan.FromSeconds(60), ct)));
            Assert.Equal(51, await client.IncrementAsync("p", 1, TimeSpan.FromSeconds(60), ct));
        }
        finally
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync(CancellationToken.None);
        }

        static async Task<long> ReadTtlAsync(int port)
        {
            await using var mux = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync($"127.0.0.1:{port}");
            var ttl = await mux.GetDatabase().KeyTimeToLiveAsync("c");
            Assert.NotNull(ttl);
            return (long)ttl.Value.TotalSeconds;
        }
    }

    private static async Task WaitReadyAsync(StackExchangeValkeyClient client, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                await client.PingAsync(ct);
                return;
            }
            catch (Exception) when (attempt < 49)
            {
                await Task.Delay(100, ct);
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string? FindServer()
    {
        foreach (var name in new[] { "valkey-server", "redis-server" })
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
