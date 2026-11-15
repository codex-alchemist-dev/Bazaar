using Bazaar.Conduit.Security;

namespace Bazaar.Conduit.Tests.Security;

public sealed class ConduitAccessOptionsTests
{
    [Fact]
    public void Shipped_IsValidAndWithinFloor()
    {
        Assert.Empty(ConduitAccessOptions.Shipped.Validate());
        Assert.Empty(ConduitAccessOptions.Shipped.ValidateAgainstFloor());
    }

    [Fact]
    public void Shipped_UsesDocumentedDefaults()
    {
        var options = ConduitAccessOptions.Shipped;

        Assert.Equal(20, options.RequestLimit);
        Assert.Equal(TimeSpan.FromSeconds(1), options.RequestWindow);
        Assert.Equal(5, options.FailureThreshold);
        Assert.Equal(TimeSpan.FromMinutes(15), options.FailureWindow);
        Assert.Equal(TimeSpan.FromMinutes(15), options.BaseLockout);
        Assert.Equal(TimeSpan.FromHours(24), options.MaxLockout);
    }

    [Theory]
    [InlineData(nameof(ConduitAccessOptions.RequestLimit))]
    [InlineData(nameof(ConduitAccessOptions.RequestWindow))]
    [InlineData(nameof(ConduitAccessOptions.FailureThreshold))]
    [InlineData(nameof(ConduitAccessOptions.FailureWindow))]
    [InlineData(nameof(ConduitAccessOptions.BaseLockout))]
    [InlineData(nameof(ConduitAccessOptions.MaxLockout))]
    public void Validate_RejectsZeroOrNegativeValue(string property)
    {
        var zero = property switch
        {
            nameof(ConduitAccessOptions.RequestLimit) => ConduitAccessOptions.Shipped with { RequestLimit = 0 },
            nameof(ConduitAccessOptions.RequestWindow) => ConduitAccessOptions.Shipped with { RequestWindow = TimeSpan.Zero },
            nameof(ConduitAccessOptions.FailureThreshold) => ConduitAccessOptions.Shipped with { FailureThreshold = -1 },
            nameof(ConduitAccessOptions.FailureWindow) => ConduitAccessOptions.Shipped with { FailureWindow = TimeSpan.Zero },
            nameof(ConduitAccessOptions.BaseLockout) => ConduitAccessOptions.Shipped with { BaseLockout = TimeSpan.Zero, MaxLockout = TimeSpan.FromHours(1) },
            _ => ConduitAccessOptions.Shipped with { MaxLockout = TimeSpan.Zero, BaseLockout = TimeSpan.Zero },
        };

        Assert.Contains(zero.Validate(), problem => problem.Contains(property, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsBaseLockoutLongerThanMax()
    {
        var options = ConduitAccessOptions.Shipped with
        {
            BaseLockout = TimeSpan.FromHours(2),
            MaxLockout = TimeSpan.FromHours(1),
        };

        Assert.Contains(options.Validate(), problem => problem.Contains("BaseLockout must not exceed MaxLockout", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsBaseEqualToMax()
    {
        var options = ConduitAccessOptions.Shipped with
        {
            BaseLockout = TimeSpan.FromHours(1),
            MaxLockout = TimeSpan.FromHours(1),
        };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Floor_RejectsThresholdAboveShippedMaximum()
    {
        var options = ConduitAccessOptions.Shipped with { FailureThreshold = ConduitAccessFloor.MaxFailureThreshold + 1 };

        Assert.Single(options.ValidateAgainstFloor());
    }

    [Fact]
    public void Floor_AcceptsThresholdAtShippedMaximum()
    {
        var options = ConduitAccessOptions.Shipped with { FailureThreshold = ConduitAccessFloor.MaxFailureThreshold };

        Assert.Empty(options.ValidateAgainstFloor());
    }

    [Fact]
    public void Floor_RejectsMaxLockoutAboveTwentyFourHours()
    {
        var options = ConduitAccessOptions.Shipped with { MaxLockout = TimeSpan.FromHours(25), BaseLockout = TimeSpan.FromHours(1) };

        Assert.Single(options.ValidateAgainstFloor());
    }

    [Fact]
    public void Floor_RejectsRateAboveShippedCeiling()
    {
        var options = ConduitAccessOptions.Shipped with { RequestLimit = 21, RequestWindow = TimeSpan.FromSeconds(1) };

        Assert.Single(options.ValidateAgainstFloor());
    }

    [Fact]
    public void Floor_ComparesRateAcrossWindowLengths()
    {
        // 40 requests in 2 s is 20 per second, which is at the ceiling and allowed.
        var atCeiling = ConduitAccessOptions.Shipped with { RequestLimit = 40, RequestWindow = TimeSpan.FromSeconds(2) };
        // 41 requests in 2 s is above the ceiling.
        var above = ConduitAccessOptions.Shipped with { RequestLimit = 41, RequestWindow = TimeSpan.FromSeconds(2) };

        Assert.Empty(atCeiling.ValidateAgainstFloor());
        Assert.Single(above.ValidateAgainstFloor());
    }

    [Fact]
    public void Floor_AllowsTighterValues()
    {
        var tighter = ConduitAccessOptions.Shipped with
        {
            RequestLimit = 5,
            FailureThreshold = 3,
            BaseLockout = TimeSpan.FromHours(1),
            MaxLockout = TimeSpan.FromHours(2),
        };

        Assert.Empty(tighter.Validate());
        Assert.Empty(tighter.ValidateAgainstFloor());
    }
}
