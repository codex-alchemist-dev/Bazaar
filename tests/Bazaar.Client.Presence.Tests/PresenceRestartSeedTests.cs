using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Chat;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Presence;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Presence.Sources;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Client.Presence.Tests;

/// <summary>The hosted service rebuilds rows from the seed source and the client chip shows them (docs/design/23, "Restart").</summary>
public class PresenceRestartSeedTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a3");
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

    private sealed class OneOpenTurn : IOpenTurnReader
    {
        public ValueTask<IReadOnlyList<OpenTurn>> ListOpenAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<OpenTurn>>([new OpenTurn(_agent, Guid.NewGuid(), _t)]);
    }

    private static string[] Texts(PresenceChipAdornments chip) => [.. chip.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    [AvaloniaFact]
    public async Task The_hosted_service_rebuilds_a_row_from_the_seed_and_the_chip_shows_it_then_unknown_after_ten_quiet_minutes()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider(_t);
        var options = new PresenceOptions { IdleLabel = "idle" };
        var store = new InMemoryPresenceStore();
        var receiver = new PresenceReceiver(_ => Task.FromResult(new PresenceSnapshot([])));
        var projector = new ActivityProjector(store, new DirectPublisher(receiver, store), time, options);

        var registry = new PresenceAdornmentRegistry();
        using var adornment = new PresenceChipAdornment(receiver);
        registry.Add(adornment);
        using var chip = new PresenceChipAdornments(registry, _agent);

        var seeds = new ReadersPresenceSeedSource(time, turns: new OneOpenTurn());
        var service = new PresenceHostedService(projector, options, time, seeds);
        await service.StartAsync(ct);
        try
        {
            await Task.Delay(50, ct);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(PresenceAvailability.OnShift, (await store.GetAsync(_agent, ct))!.Availability);
            Assert.Equal(["thinking"], Texts(chip));

            time.Advance(options.UnknownAfter + TimeSpan.FromSeconds(1));
            await projector.SweepAsync(ct);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(["unknown"], Texts(chip));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
