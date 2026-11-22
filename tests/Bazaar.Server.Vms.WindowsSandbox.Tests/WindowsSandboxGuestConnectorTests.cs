using Bazaar.Contracts.Vms;
using Bazaar.Server.VmTransport;

namespace Bazaar.Server.Vms.WindowsSandbox.Tests;

public sealed class WindowsSandboxGuestConnectorTests : IAsyncDisposable
{
    private static readonly Guid _hypervisorId = new("add62adb-34a9-425d-befe-54bedad0ee78");
    private static readonly Guid _service = new("00001388-facb-11e6-bd58-64006a7986d3");
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(20);

    private readonly FakeWindowsSandboxHost _host = new();
    private readonly LoopbackListener _listener = new();

    public ValueTask DisposeAsync() => _listener.DisposeAsync();

    private WindowsSandboxGuestConnector Connector(Guid? vmId = null, TimeSpan? connectTimeout = null) =>
        new(
            _host,
            new FixedVmId(vmId ?? _hypervisorId),
            new GuestChannelFactory(new GuestChannelOptions(), TimeProvider.System),
            new WindowsSandboxGuestOptions(_service, [@"C:\work"], ConnectTimeout: connectTimeout),
            _ => _listener);

    private static string Arg(IReadOnlyList<string> command, string name) => command[command.ToList().IndexOf(name) + 1];

    /// <summary>The guest: when exec starts the agent, dial in as the hypervisor-reported peer and send a Hello.</summary>
    private void GuestSaysHello(string? helloVmId = null, string? helloNonce = null, string peer = "add62adb-34a9-425d-befe-54bedad0ee78")
    {
        _host.ExecHandler = async (command, ct) =>
        {
            var end = _listener.Connect(peer);
            await GuestFrameCodec.WriteAsync(end.ToHost, new Hello("0.1.0", helloVmId ?? Arg(command, "--vm-id"), helloNonce ?? Arg(command, "--nonce")), ct);
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new SandboxExecResult(0, string.Empty, string.Empty);
        };
    }

    [Fact]
    public async Task A_matching_hello_opens_the_channel_and_the_agent_is_started_with_the_hvsock_options()
    {
        GuestSaysHello();

        await using var channel = await Connector().ConnectAsync("wsb-1", "vm-1", TestContext.Current.CancellationToken).WaitAsync(_bound, TestContext.Current.CancellationToken);

        Assert.Equal(GuestChannelState.Open, channel.State);
        var exec = Assert.Single(_host.Calls, c => c.StartsWith("exec:", StringComparison.Ordinal));
        Assert.StartsWith(@"exec:wsb-1:C:\Bazaar\agent\Bazaar.Guest.Agent.exe --transport hvsock --hvsock-service-id 00001388-facb-11e6-bd58-64006a7986d3 --vm-id vm-1 --nonce ", exec, StringComparison.Ordinal);
        Assert.EndsWith(@"--root C:\work", exec, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hello_with_the_wrong_nonce_is_refused()
    {
        GuestSaysHello(helloNonce: "not-the-nonce");

        await Assert.ThrowsAsync<GuestHandshakeException>(
            () => Connector().ConnectAsync("wsb-1", "vm-1", TestContext.Current.CancellationToken).WaitAsync(_bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_hello_naming_another_vm_is_refused()
    {
        GuestSaysHello(helloVmId: "vm-other");

        await Assert.ThrowsAsync<GuestHandshakeException>(
            () => Connector().ConnectAsync("wsb-1", "vm-1", TestContext.Current.CancellationToken).WaitAsync(_bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_agent_that_exits_before_it_connects_fails_closed()
    {
        _host.ExecHandler = (_, _) => Task.FromResult(new SandboxExecResult(5, string.Empty, "boom"));

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => Connector().ConnectAsync("wsb-1", "vm-1", TestContext.Current.CancellationToken).WaitAsync(_bound, TestContext.Current.CancellationToken));

        Assert.Contains("code 5", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_never_connects_times_out()
    {
        _host.ExecHandler = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new SandboxExecResult(0, string.Empty, string.Empty);
        };

        await Assert.ThrowsAsync<TimeoutException>(
            () => Connector(connectTimeout: TimeSpan.FromMilliseconds(300)).ConnectAsync("wsb-1", "vm-1", TestContext.Current.CancellationToken).WaitAsync(_bound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_sandbox_vm_id_stops_before_anything_starts()
    {
        var connector = new WindowsSandboxGuestConnector(
            _host,
            new ThrowingVmId(),
            new GuestChannelFactory(new GuestChannelOptions(), TimeProvider.System),
            new WindowsSandboxGuestOptions(_service, [@"C:\work"]),
            _ => _listener);

        await Assert.ThrowsAsync<WindowsSandboxVmIdException>(() => connector.ConnectAsync("wsb-1", "vm-1", TestContext.Current.CancellationToken));
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("exec:", StringComparison.Ordinal));
    }

    [Fact]
    public void An_agent_without_a_root_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => new WindowsSandboxGuestConnector(
                _host,
                new FixedVmId(_hypervisorId),
                new GuestChannelFactory(new GuestChannelOptions(), TimeProvider.System),
                new WindowsSandboxGuestOptions(_service, [])));
    }

    private sealed class FixedVmId(Guid id) : IWindowsSandboxVmIdSource
    {
        public Task<Guid> FindRunningAsync(CancellationToken ct) => Task.FromResult(id);
    }

    private sealed class ThrowingVmId : IWindowsSandboxVmIdSource
    {
        public Task<Guid> FindRunningAsync(CancellationToken ct) => throw new WindowsSandboxVmIdException("none");
    }
}

/// <summary>The real thing, opt-in on the owner's PC (spike S-WSB step 9). Needs an elevated process and a published agent.</summary>
public sealed class WindowsSandboxLiveChannelTests
{
    [Fact]
    public async Task Live_guest_channel_round_trip_runs_only_when_explicitly_enabled()
    {
        var agentDirectory = Environment.GetEnvironmentVariable("BAZAAR_WSB_LIVE_AGENT_DIR");
        var serviceId = Guid.Empty;
        var service = Environment.GetEnvironmentVariable("BAZAAR_WSB_LIVE_SERVICE_ID");
        if (Environment.GetEnvironmentVariable("BAZAAR_WSB_LIVE") != "1" || string.IsNullOrEmpty(agentDirectory) || !Guid.TryParse(service, out serviceId))
        {
            Assert.Skip("Set BAZAAR_WSB_LIVE=1, BAZAAR_WSB_LIVE_AGENT_DIR (a published agent) and BAZAAR_WSB_LIVE_SERVICE_ID (a registered service GUID), and run non-elevated (wsb start fails when elevated); set BAZAAR_WSB_LIVE_ELEVATE=sudo.exe so only the hcsdiag call is elevated.");
        }

        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bazaar-wsb-live-" + Guid.NewGuid().ToString("N"));
        var host = new Bazaar.Server.Vms.WindowsSandbox.WsbCli.WsbCliSandboxHost(new Bazaar.Server.Vms.WindowsSandbox.WsbCli.ProcessWsbCommandRunner(), directory);
        var spec = new Bazaar.Server.Vms.Lifecycle.VmSpec(Bazaar.Server.Vms.Environments.VmOs.Windows, 2, 4096, 60);
        var path = await host.WriteConfigAsync("live-channel", WindowsSandboxConfig.Build(spec, WindowsSandboxNetworking.Disable, agentDirectory), ct);
        var id = await host.LaunchAsync(path, ct);
        try
        {
            var connector = new WindowsSandboxGuestConnector(
                host,
                new HcsDiagVmIdSource(Environment.GetEnvironmentVariable("BAZAAR_WSB_LIVE_ELEVATE") is { Length: > 0 } elevate ? new ProcessHcsDiagRunner(elevate, ["hcsdiag.exe"]) : new ProcessHcsDiagRunner()),
                new GuestChannelFactory(new GuestChannelOptions(), TimeProvider.System),
                new WindowsSandboxGuestOptions(serviceId, [@"C:\Windows\Temp"]));

            await using var channel = await connector.ConnectAsync(id, "live-vm", ct);

            var output = new System.Text.StringBuilder();
            var result = await channel.ExecAsync(
                new ExecRequest([@"C:\Windows\System32\cmd.exe", "/c", "echo", "bazaar-live"], @"C:\Windows\Temp", [], TimeSpan.FromSeconds(30)),
                chunk =>
                {
                    output.Append(chunk.Text);
                    return Task.CompletedTask;
                },
                ct);

            Assert.True(result.Succeeded);
            Assert.Contains("bazaar-live", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await host.ShutdownAsync(id, force: true, CancellationToken.None);
            await host.DeleteConfigAsync(path, CancellationToken.None);
        }
    }
}
