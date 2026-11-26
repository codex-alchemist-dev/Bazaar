using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal.Tests;

public class BrokerAgentScopeTests
{
    [Fact]
    public async Task Broker_opens_through_the_agent_scoped_overload_with_the_agent_id()
    {
        await using var h = new Harness();
        var recording = new RecordingChannel();
        await using var broker = new TerminalBroker(recording, new DefaultTerminalAccessPolicy(new NoTerminalGrants()), h.Audit, null, h.Time);

        var (result, _) = await broker.OpenAsync(Harness.Owner, Harness.Agent1.MemberId, "vm-1", 80, 24, Harness.Ct);

        Assert.True(result.Ok);
        Assert.Equal((Harness.Agent1.MemberId, "vm-1"), recording.Scoped.Single());
    }

    [Fact]
    public async Task The_default_scoped_overload_falls_back_to_the_plain_open()
    {
        var fake = new FakeChannel();
        IGuestPtyChannel channel = fake;

        var pty = await channel.OpenForAgentAsync("agent-1", "vm-1", "t1", 80, 24, Harness.Ct);

        Assert.Same(fake.Last, pty);
    }

    private sealed class RecordingChannel : IGuestPtyChannel
    {
        public bool IsAvailable => true;

        public List<(string AgentId, string VmId)> Scoped { get; } = [];

        public Task<IGuestPty> OpenAsync(string vmId, string terminalId, int cols, int rows, CancellationToken ct) =>
            throw new InvalidOperationException("the broker must use the agent-scoped overload");

        public Task<IGuestPty> OpenForAgentAsync(string agentId, string vmId, string terminalId, int cols, int rows, CancellationToken ct)
        {
            Scoped.Add((agentId, vmId));
            return Task.FromResult<IGuestPty>(new FakeGuestPty());
        }
    }
}
