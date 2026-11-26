using Bazaar.Server.Terminal.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Terminal.Vms.Tests;

/// <summary>The adapter under the real terminal broker and the component's own registration.</summary>
public class BrokerEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NoAudit : ITerminalAudit
    {
        public ValueTask RecordAsync(string kind, TerminalCaller caller, string terminalId, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private static TerminalBroker NewBroker(Rig rig) =>
        new(new VmGuestPtyChannel(rig.Directory), new DefaultTerminalAccessPolicy(new NoTerminalGrants()), new NoAudit());

    private static TerminalCaller Agent(Guid id) => new(id.ToString("D"), IsOwner: false, IsAgent: true);

    [Fact]
    public async Task An_agent_opens_types_sees_ordered_output_and_the_exit_notice()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        await using var broker = NewBroker(rig);
        var caller = Agent(Rig.AgentA);

        var (opened, info) = await broker.OpenAsync(caller, Rig.AgentA.ToString("D"), vm.ToString("D"), 80, 24, Ct);
        Assert.True(opened.Ok);
        await using var sub = (await broker.SubscribeAsync(caller, info!.TerminalId, 0, Ct)).Subscription!;
        Assert.True((await broker.WriteAsync(caller, info.TerminalId, "ls\n"u8.ToArray(), Ct)).Ok);
        Assert.True((await broker.ResizeAsync(caller, info.TerminalId, 100, 30, Ct)).Ok);

        guest.Ptys[0].Emit("a");
        guest.Ptys[0].Emit("b");
        guest.Ptys[0].Exit(3);

        var seqs = new List<long>();
        var text = new List<string>();
        await foreach (var chunk in sub.Output.ReadAllAsync(Ct))
        {
            seqs.Add(chunk.Seq);
            text.Add(System.Text.Encoding.UTF8.GetString(chunk.Bytes.Span));
        }

        Assert.Equal(["a", "b"], text);
        Assert.Equal(seqs.OrderBy(s => s), seqs);
        Assert.Equal(3, await sub.Exited.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("ls\n"u8.ToArray(), guest.Ptys[0].Writes.SelectMany(w => w).ToArray());
        Assert.Equal([(100, 30)], guest.Ptys[0].Resizes);
    }

    [Fact]
    public async Task Close_closes_the_guest_terminal()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        await using var broker = NewBroker(rig);
        var caller = Agent(Rig.AgentA);
        var (_, info) = await broker.OpenAsync(caller, Rig.AgentA.ToString("D"), vm.ToString("D"), 80, 24, Ct);

        Assert.True((await broker.CloseAsync(caller, info!.TerminalId, Ct)).Ok);

        Assert.True(guest.Ptys[0].CloseCount >= 1, "the guest terminal was closed");
    }

    [Fact]
    public async Task Opening_in_a_vm_that_is_not_running_is_refused_by_the_broker()
    {
        var rig = new Rig();
        var (vm, guest) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        guest.End();
        await using var broker = NewBroker(rig);

        var (result, info) = await broker.OpenAsync(Agent(Rig.AgentA), Rig.AgentA.ToString("D"), vm.ToString("D"), 80, 24, Ct);

        Assert.False(result.Ok);
        Assert.Null(info);
        Assert.Empty(broker.List(new TerminalCaller("owner", IsOwner: true, IsAgent: false)));
    }

    [Fact]
    public async Task The_broker_cannot_be_used_to_reach_another_agents_vm()
    {
        var rig = new Rig();
        var (vmA, guestA) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        rig.AddRunningVm(Rig.AgentB, "vm-b");
        await using var broker = NewBroker(rig);

        // Agent B names its own id (so the access policy passes) and agent A's VM.
        var (result, info) = await broker.OpenAsync(Agent(Rig.AgentB), Rig.AgentB.ToString("D"), vmA.ToString("D"), 80, 24, Ct);

        Assert.False(result.Ok);
        Assert.Null(info);
        Assert.Empty(guestA.Opened);
    }

    [Fact]
    public async Task The_component_replaces_the_no_backend_default_and_falls_closed_without_a_directory()
    {
        var services = new ServiceCollection();
        services.AddBazaarTerminal();
        new TerminalVmsComponent().AddServices(services, new ConfigurationBuilder().Build());
        await using var withoutDirectory = services.BuildServiceProvider();

        var channel = withoutDirectory.GetRequiredService<IGuestPtyChannel>();
        Assert.IsType<VmGuestPtyChannel>(channel);
        Assert.False(channel.IsAvailable);

        var rig = new Rig();
        var (vm, _) = rig.AddRunningVm(Rig.AgentA, "vm-a");
        services.TryAddSingleton<VmTransport.IGuestChannelDirectory>(rig.Directory);
        await using var withDirectory = services.BuildServiceProvider();
        Assert.True(withDirectory.GetRequiredService<IGuestPtyChannel>().IsAvailable);

        var (result, _) = await withDirectory.GetRequiredService<ITerminalBroker>()
            .OpenAsync(Agent(Rig.AgentA), Rig.AgentA.ToString("D"), vm.ToString("D"), 80, 24, Ct);
        Assert.True(result.Ok);
    }

    [Fact]
    public void The_manifest_orders_the_adapter_after_the_terminal_module()
    {
        var manifest = TerminalVmsComponent.ManifestDefinition;

        Assert.Equal("terminal-vms", manifest.Id);
        Assert.Contains("terminal", manifest.DependsOn);
        Assert.Contains("vms", manifest.Soft);
    }
}
