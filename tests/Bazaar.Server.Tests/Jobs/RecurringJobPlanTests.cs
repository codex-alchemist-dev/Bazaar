using Bazaar.Server.Jobs;

namespace Bazaar.Server.Tests.Jobs;

/// <summary>The bucket rules behind the recurring-job seam (doc 03, Background jobs). Pure: no database.</summary>
public sealed class RecurringJobPlanTests
{
    // 1_800_000_000 is a whole minute, so one-minute buckets start on it.
    private static readonly DateTimeOffset _minute = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly RecurringJob _everyMinute = new("ryusei.trigger_tick", TimeSpan.FromMinutes(1));

    [Fact]
    public void Moments_in_the_same_bucket_share_one_key_and_one_due_time()
    {
        var first = RecurringJobPlan.Plan(_everyMinute, _minute);
        var later = RecurringJobPlan.Plan(_everyMinute, _minute.AddSeconds(59));

        Assert.Equal(first.DedupeKey, later.DedupeKey);
        Assert.Equal(first.RunAt, later.RunAt);
    }

    [Fact]
    public void The_due_time_is_the_bucket_start_not_the_moment_polled()
    {
        var planned = RecurringJobPlan.Plan(_everyMinute, _minute.AddSeconds(42));

        Assert.Equal(_minute, planned.RunAt);
    }

    [Fact]
    public void The_next_bucket_gets_a_new_key()
    {
        var now = RecurringJobPlan.Plan(_everyMinute, _minute);
        var next = RecurringJobPlan.Plan(_everyMinute, _minute.AddMinutes(1));

        Assert.NotEqual(now.DedupeKey, next.DedupeKey);
        Assert.Equal(_minute.AddMinutes(1), next.RunAt);
    }

    [Fact]
    public void The_key_names_the_kind_and_the_interval()
    {
        var planned = RecurringJobPlan.Plan(_everyMinute, _minute);
        var fiveMinutes = RecurringJobPlan.Plan(new RecurringJob("ryusei.trigger_tick", TimeSpan.FromMinutes(5)), _minute);
        var otherKind = RecurringJobPlan.Plan(new RecurringJob("kanban.deadline_scan", TimeSpan.FromMinutes(1)), _minute);

        Assert.Equal($"recurring:ryusei.trigger_tick:60:{_minute.ToUnixTimeSeconds()}", planned.DedupeKey);
        Assert.NotEqual(planned.DedupeKey, fiveMinutes.DedupeKey);
        Assert.NotEqual(planned.DedupeKey, otherKind.DedupeKey);
    }

    [Fact]
    public void The_payload_is_carried_through()
    {
        var job = new RecurringJob("kanban.deadline_scan", TimeSpan.FromMinutes(5), "{\"batch\":10}");

        Assert.Equal("{\"batch\":10}", RecurringJobPlan.Plan(job, _minute).PayloadJson);
    }

    [Fact]
    public void A_scheduler_refuses_two_jobs_of_the_same_kind()
    {
        var jobs = new[]
        {
            new RecurringJob("ryusei.trigger_tick", TimeSpan.FromMinutes(1)),
            new RecurringJob("ryusei.trigger_tick", TimeSpan.FromMinutes(5)),
        };

        Assert.Throws<ArgumentException>(() => new RecurringJobScheduler(new JobStore(null!), jobs));
    }
}
