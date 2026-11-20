using Bazaar.Server.Jobs;

namespace Bazaar.Server.Tests.Jobs;

public sealed class JobBackoffTests
{
    private static readonly JobPollerOptions _options = new()
    {
        RetryBaseDelay = TimeSpan.FromSeconds(10),
        RetryMaxDelay = TimeSpan.FromMinutes(5),
    };

    [Fact]
    public void First_failure_waits_the_base_delay_then_doubles()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), _options.RetryDelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(20), _options.RetryDelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(40), _options.RetryDelayFor(3));
    }

    [Fact]
    public void Delay_is_capped_at_the_max()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), _options.RetryDelayFor(20));
    }

    [Fact]
    public void Very_large_attempt_counts_do_not_overflow()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), _options.RetryDelayFor(int.MaxValue));
    }

    [Fact]
    public void Zero_base_delay_retries_immediately()
    {
        var options = new JobPollerOptions { RetryBaseDelay = TimeSpan.Zero, RetryMaxDelay = TimeSpan.Zero };
        Assert.Equal(TimeSpan.Zero, options.RetryDelayFor(4));
    }

    [Fact]
    public void Attempts_below_one_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _options.RetryDelayFor(0));
    }

    [Fact]
    public void Invalid_options_are_rejected_with_the_offending_name()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new JobPollerOptions { BatchSize = 0 }.Validate());
        Assert.Equal("BatchSize", ex.ParamName);

        Assert.Throws<ArgumentOutOfRangeException>(() => new JobPollerOptions { PollInterval = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobPollerOptions { MaxAttempts = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new JobPollerOptions
        {
            RetryBaseDelay = TimeSpan.FromMinutes(2),
            RetryMaxDelay = TimeSpan.FromMinutes(1),
        }.Validate());
    }
}
