using Bazaar.Server.Security.Approvals;

namespace Bazaar.Server.Tests.Security.Approvals;

public class ApprovalOptionsTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(169, 10)]
    [InlineData(24, 0)]
    [InlineData(24, 101)]
    public void Validate_rejects_values_outside_the_range(int expiryHours, int requestCap)
    {
        var options = new ApprovalOptions(expiryHours, requestCap);

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(168, 100)]
    [InlineData(1, 100)]
    [InlineData(168, 1)]
    public void Validate_accepts_the_boundaries(int expiryHours, int requestCap)
    {
        var options = new ApprovalOptions(expiryHours, requestCap);

        options.Validate();
    }
}
