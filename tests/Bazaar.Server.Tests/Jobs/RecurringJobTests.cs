using Bazaar.Server.Jobs;

namespace Bazaar.Server.Tests.Jobs;

/// <summary>The recurring-job seam (doc 03, Background jobs): interval validation and the bucket rules. No database.</summary>
public sealed class RecurringJobTests
{
    [Fact]
    public void Interval_below_one_second_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecurringJob("kanban.deadline_scan", TimeSpan.FromMilliseconds(500)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecurringJob("kanban.deadline_scan", TimeSpan.Zero));
    }

    [Fact]
    public void Interval_with_fractional_seconds_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecurringJob("kanban.deadline_scan", TimeSpan.FromMilliseconds(1500)));
    }

    [Fact]
    public void Blank_kind_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new RecurringJob(" ", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Whole_second_interval_is_accepted_with_an_empty_payload_by_default()
    {
        var job = new RecurringJob("ryusei.trigger_tick", TimeSpan.FromMinutes(1));
        Assert.Equal("{}", job.PayloadJson);
    }

}
