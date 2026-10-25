using Bazaar.Testing;

namespace Bazaar.Server.Tests.Testing;

public class SeededRunTests
{
    [Fact]
    public void Returns_the_body_result_and_logs_nothing_on_success()
    {
        var log = new StringWriter();

        var result = SeededRun.Execute(new TestSeed(5), seed => seed.Value * 2, log: log);

        Assert.Equal(10UL, result);
        Assert.Empty(log.ToString());
    }

    [Fact]
    public void Failure_logs_the_seed_and_the_replay_hint()
    {
        var log = new StringWriter();

        var error = Assert.Throws<InvalidOperationException>(() =>
            SeededRun.Execute(new TestSeed(4242), _ => throw new InvalidOperationException("boom"), log: log));

        Assert.Equal("boom", error.Message);
        var text = log.ToString();
        Assert.Contains("seed=4242", text);
        Assert.Contains("BAZAAR_TEST_SEED=4242", text);
    }

    [Fact]
    public void Failure_also_logs_the_clock_when_one_is_given()
    {
        var log = new StringWriter();
        var clock = new FakeClock();
        clock.Advance(TimeSpan.FromHours(2));

        Assert.Throws<InvalidOperationException>(() =>
            SeededRun.Execute(new TestSeed(1), _ => throw new InvalidOperationException(), clock, log));

        Assert.Contains("clock=FakeClock(utc=2026-01-01T02:00:00.0000000+00:00", log.ToString());
    }

    [Fact]
    public void Failure_rethrows_the_same_exception_instance_with_the_seed_in_Data()
    {
        var original = new InvalidOperationException("same");
        Exception? caught = null;

        try
        {
            SeededRun.Execute(new TestSeed(8), _ => throw original, log: new StringWriter());
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        Assert.Same(original, caught);
        Assert.Equal(8UL, original.Data[SeededRun.SeedDataKey]);
    }

    [Fact]
    public void Action_overload_passes_the_seed_to_the_body()
    {
        ulong seen = 0;

        SeededRun.Execute(new TestSeed(31), seed => seen = seed.Value, log: new StringWriter());

        Assert.Equal(31UL, seen);
    }

    [Fact]
    public void Action_overload_logs_on_failure()
    {
        var log = new StringWriter();

        Assert.Throws<InvalidOperationException>(() =>
            SeededRun.Execute(new TestSeed(6), _ => throw new InvalidOperationException(), log: log));

        Assert.Contains("seed=6", log.ToString());
    }
}
