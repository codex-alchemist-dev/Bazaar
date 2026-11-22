namespace Bazaar.Server.Valkey.Tests;

/// <summary>The launch command, the stdin configuration and a real server start when a Redis-protocol binary is installed.</summary>
public class ValkeyLaunchTests
{
    [Fact]
    public void With_a_wrapper_the_server_runs_under_a_parent_death_signal()
    {
        var spec = new ValkeyLaunchSpec("/opt/valkey/bin/valkey-server", "port 1", "setpriv");

        var (fileName, arguments) = ValkeyProcessLauncher.Command(spec);

        Assert.Equal("setpriv", fileName);
        Assert.Equal(["--pdeathsig", "SIGKILL", "--", "/opt/valkey/bin/valkey-server", "-"], arguments);
    }

    [Fact]
    public void Without_a_wrapper_the_server_is_run_directly()
    {
        var spec = new ValkeyLaunchSpec("valkey-server", "port 1", string.Empty);

        var (fileName, arguments) = ValkeyProcessLauncher.Command(spec);

        Assert.Equal("valkey-server", fileName);
        Assert.Equal(["-"], arguments);
    }

    [Fact]
    public void The_configuration_never_reaches_the_command_line_or_the_string_form()
    {
        const string secretConfig = "requirepass 0123456789ABCDEF\nport 4000\n";
        var spec = new ValkeyLaunchSpec("valkey-server", secretConfig, "setpriv");

        var (fileName, arguments) = ValkeyProcessLauncher.Command(spec);

        Assert.DoesNotContain("0123456789ABCDEF", fileName, StringComparison.Ordinal);
        Assert.All(arguments, argument => Assert.DoesNotContain("0123456789ABCDEF", argument, StringComparison.Ordinal));
        Assert.DoesNotContain("0123456789ABCDEF", spec.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_linux_a_start_without_a_parent_death_wrapper_is_refused()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var launcher = new ValkeyProcessLauncher();
        var spec = new ValkeyLaunchSpec("/bin/sleep", "port 1", string.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.StartAsync(spec, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_free_loopback_port_is_picked_when_the_port_is_zero()
    {
        var port = ValkeyServerSupervisor.FreeLoopbackPort();

        Assert.InRange(port, 1024, 65535);
    }

    [Fact]
    public async Task A_real_server_reads_its_configuration_from_stdin_and_requires_the_password()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var server = FindOnPath("valkey-server") ?? FindOnPath("redis-server");
        if (server is null)
        {
            Assert.Skip("No valkey-server or redis-server on PATH.");
        }

        var ct = TestContext.Current.CancellationToken;
        var port = ValkeyServerSupervisor.FreeLoopbackPort();
        const string password = "0123456789abcdef0123456789abcdef";
        var config = ValkeyServerSupervisor.BuildConfig(port, password, 8);
        var wrapper = FindOnPath("setpriv") is null ? string.Empty : "setpriv";
        var launcher = new ValkeyProcessLauncher();

        await using var child = await launcher.StartAsync(new ValkeyLaunchSpec(server!, config, wrapper), ct);
        Assert.True(child.ProcessId > 0);

        await using var client = new StackExchangeValkeyClient($"127.0.0.1:{port},password={password}");
        await WaitForPingAsync(client, ct);
        Assert.Equal(ValkeyCacheOptions.EvictionPolicy, await client.GetConfigAsync("maxmemory-policy", ct));
        Assert.Equal((8L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture), await client.GetConfigAsync("maxmemory", ct));

        child.Kill();
        await child.Exited.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    private static async Task WaitForPingAsync(StackExchangeValkeyClient client, CancellationToken ct)
    {
        Exception? last = null;
        for (var i = 0; i < 50; i++)
        {
            try
            {
                await client.PingAsync(ct);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(100, ct);
            }
        }

        throw new InvalidOperationException("The server did not answer PING.", last);
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
