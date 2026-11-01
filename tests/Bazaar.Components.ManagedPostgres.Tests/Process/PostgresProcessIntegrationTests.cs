using System.Net.Sockets;

namespace Bazaar.Components.ManagedPostgres.Tests;

/// <summary>Runs a real PostgreSQL 18 from BAZAAR_TEST_PG_BIN. Skipped when that variable is not set.</summary>
public sealed class PostgresProcessIntegrationTests : IDisposable
{
    private const string BinVariable = "BAZAAR_TEST_PG_BIN";
    private readonly string _root = ProcessTestSupport.NewTempDirectory();

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Initdb_start_readiness_and_stop_work_against_a_real_server()
    {
        var bin = Environment.GetEnvironmentVariable(BinVariable);
        if (string.IsNullOrWhiteSpace(bin))
        {
            Assert.Skip($"Set {BinVariable} to a PostgreSQL 18 bin directory to run this test.");
        }

        var dataDirectory = Path.Combine(_root, "data");
        var port = ProcessTestSupport.FreePort();
        var options = ProcessTestSupport.OptionsFor(dataDirectory, port);
        options.BinDirectory = bin;
        options.StartupTimeout = TimeSpan.FromSeconds(60);
        options.ShutdownTimeout = TimeSpan.FromSeconds(15);
        await using var controller = new PostgresProcessController(options, new ProcessPostgresCommandRunner());

        await controller.InitDbAsync((_, _) => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        await controller.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PostgresProcessState.Running, controller.State);
        Assert.StartsWith("18", File.ReadAllText(Path.Combine(dataDirectory, "PG_VERSION")), StringComparison.Ordinal);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", port, TestContext.Current.CancellationToken);
        }

        await controller.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PostgresProcessState.Stopped, controller.State);
        using var closed = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await closed.ConnectAsync("127.0.0.1", port, TestContext.Current.CancellationToken));
    }
}
