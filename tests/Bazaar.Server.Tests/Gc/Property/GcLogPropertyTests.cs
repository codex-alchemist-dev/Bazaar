using Bazaar.Testing;

namespace Bazaar.Server.Tests.Gc.Properties;

/// <summary>
/// Seeded property tests over random interleavings of GC operations (docs/design/34-testing-strategy.md, WP 10;
/// docs/design/01-gc-data-model.md, Testing). Each property runs every seed from <see cref="GcPropertySeeds"/>.
/// A failure names the seed and the operation index; replay with <see cref="TestSeed.EnvironmentVariable"/>.
/// </summary>
public sealed class GcLogPropertyTests
{
    [Fact]
    public void Seqs_are_gap_free_and_unique_across_interleavings()
    {
        RunAll(GcChecks.GapFree);
    }

    [Fact]
    public void Reads_from_any_seq_equal_the_log_suffix()
    {
        RunAll(GcChecks.Replay);
    }

    [Fact]
    public void Reader_chunks_concatenate_to_the_full_log_under_interleaved_appends()
    {
        RunAll(GcChecks.CursorInterleaving);
    }

    [Fact]
    public void Refused_or_rolled_back_appends_write_nothing()
    {
        RunAll(GcChecks.RolledBackAppends);
    }

    [Fact]
    public void Message_versions_are_contiguous_and_match_the_log()
    {
        RunAll(GcChecks.MessageHistory);
    }

    [Fact]
    public void Viewer_filter_holds_for_every_viewer_after_every_operation()
    {
        RunAll(GcChecks.ViewerFilter);
    }

    [Fact]
    public void Thinking_gc_events_stay_out_of_other_agents_context()
    {
        RunAll(GcChecks.ThinkingGc);
    }

    [Fact]
    public void Accepted_mentions_target_active_members_or_the_owner_and_rejected_ones_are_never_written()
    {
        RunAll(GcChecks.Mentions);
    }

    [Fact]
    public void Same_seed_produces_the_same_log()
    {
        foreach (var seed in GcPropertySeeds.All().Take(8))
        {
            var first = Simulate(seed, GcChecks.None);
            var second = Simulate(seed, GcChecks.None);
            Assert.True(first == second, $"seed={seed}: the same seed produced different logs. Replay with {TestSeed.EnvironmentVariable}={seed}.");
        }
    }

    [Fact]
    public void Every_property_runs_all_regression_and_sweep_seeds()
    {
        var seeds = GcPropertySeeds.All().ToList();
        Assert.True(seeds.Count >= GcPropertySeeds.SweepCount + 8, $"only {seeds.Count} seeds were scheduled");
        Assert.Equal(seeds.Count, seeds.Distinct().Count());
    }

    private static void RunAll(GcChecks checks)
    {
        foreach (var seed in GcPropertySeeds.All())
        {
            var clock = new FakeClock();
            SeededRun.Execute(seed, s => new GcSimulation(s, clock, checks).Run(GcPropertySeeds.OpsPerSequence), clock);
        }
    }

    private static string Simulate(TestSeed seed, GcChecks checks)
    {
        var clock = new FakeClock();
        var simulation = new GcSimulation(seed, clock, checks);
        SeededRun.Execute(seed, s => simulation.Run(GcPropertySeeds.OpsPerSequence), clock);
        return simulation.Fingerprint();
    }
}
