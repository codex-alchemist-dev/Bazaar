using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Chat;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Presence;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Presence.Sources;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife;
using Bazaar.Testing;

namespace Bazaar.Client.Presence.Tests;

/// <summary>Source modules to the client chip, through the adapters and the real projector (docs/design/23).</summary>
public class PresenceSourcesEndToEndTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a2");
    private static readonly DateTimeOffset _t = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

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
    public async Task An_agent_on_shift_running_a_tool_changes_the_row_and_the_chip_through_the_adapters()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryPresenceStore();
        var receiver = new PresenceReceiver(_ => Task.FromResult(new PresenceSnapshot([])));
        var projector = new ActivityProjector(store, new DirectPublisher(receiver, store), new FakeClock(), new PresenceOptions { IdleLabel = "idle" });

        var availability = new AvailabilityPresenceSource(projector);
        var turns = new TurnPresenceSource(projector, new NameTableToolClassifier(new PresenceSourcesOptions()));

        var registry = new PresenceAdornmentRegistry();
        using var adornment = new PresenceChipAdornment(receiver);
        registry.Add(adornment);
        using var chip = new PresenceChipAdornments(registry, _agent);

        var machine = new AvailabilityMachine(_agent.ToString(), AvailabilityMachineState.Off);
        await availability.OnTransitionAsync(_agent, machine.Submit(new AvailabilityEvent.ScheduleStart(_t)), _t, ct);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(PresenceAvailability.OnShift, (await store.GetAsync(_agent, ct))!.Availability);
        Assert.Equal(["idle"], Texts(chip));

        var turn = Guid.NewGuid();
        var gc = Guid.NewGuid();
        await turns.OnTurnActivityAsync(new TurnActivity(_agent, _t.AddSeconds(1), TurnActivityKind.TurnStarted, turn, gc, TurnGcKind.Project, "p1", "p1"), ct);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["thinking"], Texts(chip));

        await turns.OnTurnActivityAsync(new TurnActivity(_agent, _t.AddSeconds(2), TurnActivityKind.ToolStarted, turn, gc, TurnGcKind.Project, "p1", "p1", ToolName: "web.search"), ct);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var row = (await store.GetAsync(_agent, ct))!;
        Assert.Equal(PresenceActivityKind.Tool, row.ActivityKind);
        Assert.Equal("researching", row.Presence);
        Assert.Equal(["researching"], Texts(chip));

        await turns.OnTurnActivityAsync(new TurnActivity(_agent, _t.AddSeconds(3), TurnActivityKind.TurnEnded, turn), ct);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(["idle"], Texts(chip));
    }
}
