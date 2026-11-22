using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Chat;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Presence;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.WorkLife;
using Bazaar.Testing;

namespace Bazaar.Client.Presence.Tests;

/// <summary>Server projector to the client chip, over fakes (docs/design/23).</summary>
public class PresenceEndToEndTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset _t = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A publisher that hands each delta straight to a client receiver, as the socket would.</summary>
    private sealed class DirectPublisher(PresenceReceiver receiver, IPresenceStore store) : IPresencePublisher
    {
        public ValueTask PublishDeltaAsync(PresenceRow row, CancellationToken ct)
        {
            var wire = PresencePublisher.ToWire(row);
            receiver.Apply(new PresenceUpdate(PresenceKinds.Agent, wire.AgentId, wire.Revision, wire));
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<PresenceRow>> GetSnapshotAsync(CancellationToken ct) => store.GetAllAsync(ct);
    }

    private static string[] Texts(PresenceChipAdornments chip) => [.. chip.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    [AvaloniaFact]
    public async Task An_agent_going_on_shift_and_starting_a_turn_changes_the_presence_chip()
    {
        var store = new InMemoryPresenceStore();
        var receiver = new PresenceReceiver(_ => Task.FromResult(new PresenceSnapshot([])));
        var projector = new ActivityProjector(store, new DirectPublisher(receiver, store), new FakeClock(), new PresenceOptions { IdleLabel = "idle" });

        var registry = new PresenceAdornmentRegistry();
        using var adornment = new PresenceChipAdornment(receiver);
        registry.Add(adornment);
        using var chip = new PresenceChipAdornments(registry, _agent);
        Assert.Empty(Texts(chip));
        Assert.False(chip.IsVisible);

        await projector.OnEventAsync(ActivityEvent.AvailabilityChanged(_agent, _t, PresenceAvailability.OnShift), TestContext.Current.CancellationToken);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["idle"], Texts(chip));
        Assert.True(chip.IsVisible);

        await projector.OnEventAsync(
            ActivityEvent.TurnChanged(_agent, _t.AddSeconds(1), PresenceActivityKind.ModelCall, gcId: Guid.NewGuid(), turnId: Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["thinking"], Texts(chip));

        await projector.OnEventAsync(ActivityEvent.Flag(_agent, _t.AddSeconds(2), ActivityEventKind.QuotaThrottle, true), TestContext.Current.CancellationToken);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["thinking (throttled)"], Texts(chip));
    }

    [AvaloniaFact]
    public void The_chip_ignores_other_agents_and_shows_nothing_without_a_row()
    {
        var receiver = new PresenceReceiver(_ => Task.FromResult(new PresenceSnapshot([])));
        var registry = new PresenceAdornmentRegistry();
        using var adornment = new PresenceChipAdornment(receiver);
        registry.Add(adornment);
        using var chip = new PresenceChipAdornments(registry, _agent);

        receiver.Apply(PresenceReceiverTests.Delta(Guid.NewGuid(), 1));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Empty(Texts(chip));
    }

    [Fact]
    public async Task The_wire_client_feeds_agent_deltas_to_the_receiver_and_fetches_a_snapshot_on_connect()
    {
        var transport = new FakeWireTransport();
        var client = new WireClient(
            transport,
            transport.Codec,
            new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "linux-test", DeviceClass = "linux", ClientBuild = "test", Jitter = () => 1.0 },
            new FakeClock());
        using var receiver = PresenceWiring.Attach(client);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(cts.Token);
        try
        {
            await Until(() => transport.Count == 1 && transport.Latest.SentOfType(WireTypes.Subscribe).Any());
            Assert.Contains(transport.Latest.SentOfType(WireTypes.Subscribe), e => transport.Codec.DecodePayload<Subscribe>(e).Streams.Any(s => s.S == WireStreams.Presence));

            transport.Latest.PushEvent(WireTypes.PresenceUpdate, PresenceReceiverTests.Delta(_agent, 3, "thinking"), WireStreams.Presence);
            await Until(() => receiver.TryGetRow(_agent) is not null);
            Assert.Equal("thinking", receiver.TryGetRow(_agent)!.Presence);

            await Until(() => transport.Latest.SentOfType(WireTypes.PresenceSnapshot).Any());
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail("Condition not reached.");
    }
}
