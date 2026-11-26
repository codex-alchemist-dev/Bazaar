using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.Vms.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmDisplay.AgentTools.Vms.Tests;

public class TargetsTests
{
    private static readonly Guid _agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class ScriptedDirectory : IAgentVmDirectory
    {
        public Func<Guid, CancellationToken, Task<IReadOnlyList<AgentVmInfo>>> ForAgent { get; set; } = (_, _) => Task.FromResult<IReadOnlyList<AgentVmInfo>>([]);

        public List<Guid> Asked { get; } = [];

        public Task<IReadOnlyList<AgentVmInfo>> ListForAgentAsync(Guid agentId, CancellationToken ct)
        {
            Asked.Add(agentId);
            return ForAgent(agentId, ct);
        }

        public Task<IReadOnlyList<AgentVmInfo>> ListRunningAsync(CancellationToken ct) =>
            throw new InvalidOperationException("The adapter must only ask for the calling agent's VMs.");
    }

    private static AgentVmInfo Vm(Guid agent, VmGuestOs os, VmRunState state = VmRunState.Running, string? backendRef = "vm-name") =>
        new(Guid.CreateVersion7(), agent, os, backendRef, state);

    private static ScriptedDirectory Directory(params AgentVmInfo[] vms) =>
        new() { ForAgent = (_, _) => Task.FromResult<IReadOnlyList<AgentVmInfo>>(vms) };

    private static VmAgentDisplayTargets Adapter(IAgentVmDirectory? directory, FakeTimeProvider? time = null, int timeoutSeconds = 5) =>
        new(directory, time ?? new FakeTimeProvider(), new AgentTargetsOptions { Enabled = true, ReadTimeoutSeconds = timeoutSeconds });

    private static Task<DisplayTarget?> Resolve(VmAgentDisplayTargets adapter, AgentDisplayKind kind, Guid? agent = null, CancellationToken ct = default) =>
        adapter.ResolveAsync(agent ?? _agent, kind, ct);

    [Theory]
    [InlineData(AgentDisplayKind.Linux, VmGuestOs.Linux, DisplayGuestOs.Linux)]
    [InlineData(AgentDisplayKind.Windows, VmGuestOs.Windows, DisplayGuestOs.Windows)]
    public async Task A_running_vm_of_the_agent_with_the_requested_os_is_the_target(AgentDisplayKind kind, VmGuestOs vmOs, DisplayGuestOs displayOs)
    {
        var vm = Vm(_agent, vmOs, backendRef: "hv-name");
        var target = await Resolve(Adapter(Directory(Vm(_agent, vmOs == VmGuestOs.Linux ? VmGuestOs.Windows : VmGuestOs.Linux), vm)), kind, ct: TestContext.Current.CancellationToken);

        Assert.Equal(new DisplayTarget(_agent.ToString("D"), vm.VmId, displayOs, "hv-name"), target);
        Assert.Null(target!.BrowserSessionId);
    }

    [Fact]
    public async Task The_directory_is_asked_for_the_calling_agent_only()
    {
        var directory = Directory(Vm(_agent, VmGuestOs.Linux));

        await Resolve(Adapter(directory), AgentDisplayKind.Linux, ct: TestContext.Current.CancellationToken);

        Assert.Equal([_agent], directory.Asked);
    }

    [Theory]
    [InlineData(VmRunState.Saved)]
    [InlineData(VmRunState.Stopped)]
    [InlineData(VmRunState.Provisioning)]
    [InlineData(VmRunState.Other)]
    public async Task A_vm_that_is_not_running_is_not_a_target(VmRunState state)
    {
        var adapter = Adapter(Directory(Vm(_agent, VmGuestOs.Linux, state, backendRef: state == VmRunState.Provisioning ? null : "vm-name")));

        Assert.Null(await Resolve(adapter, AgentDisplayKind.Linux, ct: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_running_vm_without_a_backend_ref_is_not_a_target(string? backendRef)
    {
        var adapter = Adapter(Directory(Vm(_agent, VmGuestOs.Linux, backendRef: backendRef)));

        Assert.Null(await Resolve(adapter, AgentDisplayKind.Linux, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Another_agents_vm_is_never_a_target_even_if_the_directory_returns_it()
    {
        var adapter = Adapter(Directory(Vm(_other, VmGuestOs.Linux)));

        Assert.Null(await Resolve(adapter, AgentDisplayKind.Linux, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_browser_kind_and_an_empty_agent_id_get_no_target_and_no_read()
    {
        var directory = Directory(Vm(_agent, VmGuestOs.Linux));
        var adapter = Adapter(directory);

        Assert.Null(await Resolve(adapter, AgentDisplayKind.Browser, ct: TestContext.Current.CancellationToken));
        Assert.Null(await Resolve(adapter, AgentDisplayKind.Linux, Guid.Empty, TestContext.Current.CancellationToken));
        Assert.Empty(directory.Asked);
    }

    [Fact]
    public async Task No_directory_gives_no_target() =>
        Assert.Null(await Resolve(Adapter(null), AgentDisplayKind.Linux, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task An_agent_with_no_vms_gets_no_target() =>
        Assert.Null(await Resolve(Adapter(Directory()), AgentDisplayKind.Windows, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task A_failing_read_gives_no_target()
    {
        var directory = new ScriptedDirectory { ForAgent = (_, _) => Task.FromException<IReadOnlyList<AgentVmInfo>>(new InvalidOperationException("db down")) };

        Assert.Null(await Resolve(Adapter(directory), AgentDisplayKind.Linux, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_slow_read_gives_no_target_after_the_timeout()
    {
        var time = new FakeTimeProvider();
        var directory = new ScriptedDirectory { ForAgent = (_, _) => new TaskCompletionSource<IReadOnlyList<AgentVmInfo>>().Task };

        var pending = Resolve(Adapter(directory, time, timeoutSeconds: 5), AgentDisplayKind.Linux, ct: TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Null(await pending);
    }

    [Fact]
    public async Task Cancellation_propagates_and_is_not_a_verdict()
    {
        using var cts = new CancellationTokenSource();
        var directory = new ScriptedDirectory { ForAgent = (_, ct) => new TaskCompletionSource<IReadOnlyList<AgentVmInfo>>().Task.WaitAsync(ct) };

        var pending = Resolve(Adapter(directory), AgentDisplayKind.Linux, ct: cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Resolve(Adapter(directory), AgentDisplayKind.Linux, ct: cts.Token));
    }
}
