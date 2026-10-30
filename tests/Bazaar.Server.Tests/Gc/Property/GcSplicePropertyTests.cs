using Bazaar.Contracts.Wire;
using Bazaar.Server.Tests.Wire.Sync;
using Bazaar.Server.Wire.Sync;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gc.Properties;

/// <summary>
/// Seeded property for the replay-to-live splice (docs/design/02-wire-protocol-and-sync.md, Subscriptions: "appends during
/// replay give no gap or duplicate"; docs/design/34-testing-strategy.md, WP 10). A subscriber replays pages from the log while
/// appends and edits keep arriving, then splices to the live feed. Each seed runs its own random page size, buffer size,
/// preload and edit mix, through the in-process <see cref="SyncHarness"/>.
/// </summary>
public sealed class GcSplicePropertyTests
{
    [Fact]
    public async Task Appends_and_edits_during_replay_splice_to_live_with_no_gap_or_duplicate()
    {
        foreach (var seed in GcPropertySeeds.All())
        {
            try
            {
                await OneRoundAsync(seed);
            }
            catch
            {
                Console.Error.WriteLine($"Bazaar test failed: seed={seed}. Replay with {TestSeed.EnvironmentVariable}={seed}.");
                throw;
            }
        }
    }

    private static async Task OneRoundAsync(TestSeed seed)
    {
        var draw = new DeterministicIds(seed);
        using var h = new SyncHarness(new SyncOptions
        {
            ReplayPageSize = 1 + draw.NextInt(11),
            LiveBufferMax = 1 + draw.NextInt(19),
            ReplayCutoffEntries = 10_000,
        });

        var preload = draw.NextInt(40);
        for (var i = 1; i <= preload; i++)
        {
            h.Log.Append(SyncHarness.Gc, $"m{i}");
        }

        // The appender's script is drawn up front, so the seeded stream is never read from two threads.
        var total = preload + 20 + draw.NextInt(100);
        var script = new (bool Edit, int Pick, bool Yield)[total - preload];
        for (var i = 0; i < script.Length; i++)
        {
            script[i] = (draw.NextInt(4) == 0, draw.NextInt(1 << 20), draw.NextInt(3) == 0);
        }

        var appender = Task.Run(async () =>
        {
            for (var i = 0; i < script.Length; i++)
            {
                var snapshot = h.Log.Snapshot(SyncHarness.Gc);
                if (snapshot.Count > 0 && script[i].Edit)
                {
                    h.Log.Edit(SyncHarness.Gc, snapshot[script[i].Pick % snapshot.Count].MessageId, $"e{i}");
                }
                else
                {
                    h.Log.Append(SyncHarness.Gc, $"a{i}");
                }

                if (script[i].Yield)
                {
                    await Task.Yield();
                }
            }
        });

        using var c = h.Connect();
        for (var spins = draw.NextInt(30); spins > 0; spins--)
        {
            await Task.Yield();
        }

        await c.SubscribeAsync();
        await appender;

        var final = h.Log.Snapshot(SyncHarness.Gc);
        var finalEv = final.Count == 0 ? 0 : final.Max(m => m.Ev);
        await c.WaitAsync(() => finalEv == 0 || (c.Messages.Count > 0 && c.Messages.Max(m => m.Ev) == finalEv));

        var received = c.Messages;
        Assert.Equal(received.Select(m => m.Ev).Distinct().Count(), received.Count);
        Assert.Equal(received.OrderBy(m => m.Ev).Select(m => m.Ev), received.Select(m => m.Ev));

        // Nothing is invented: every delivered event is a committed event of this GC.
        var committedEvs = Enumerable.Range(1, (int)finalEv).Select(e => (long)e).ToHashSet();
        Assert.All(received, m => Assert.Contains(m.Ev, committedEvs));

        // Nothing is lost: the current version of every message arrives, at its latest event.
        var lastByMessage = received.GroupBy(m => m.MessageId).ToDictionary(g => g.Key, g => g.Last());
        Assert.Equal(final.Count, lastByMessage.Count);
        foreach (var current in final)
        {
            Assert.Equal(current.Ev, lastByMessage[current.MessageId].Ev);
        }
    }
}
