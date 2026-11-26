using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals.Tests;

public class PlatformSeamsTests
{
    [Theory]
    [InlineData(5, BiometricOutcome.Cancelled)]
    [InlineData(10, BiometricOutcome.Cancelled)]
    [InlineData(13, BiometricOutcome.Cancelled)]
    [InlineData(1, BiometricOutcome.Unavailable)]
    [InlineData(11, BiometricOutcome.Unavailable)]
    [InlineData(12, BiometricOutcome.Unavailable)]
    [InlineData(14, BiometricOutcome.Unavailable)]
    [InlineData(7, BiometricOutcome.Failed)]
    [InlineData(9, BiometricOutcome.Failed)]
    [InlineData(3, BiometricOutcome.Failed)]
    [InlineData(-1, BiometricOutcome.Failed)]
    [InlineData(9999, BiometricOutcome.Failed)]
    public void An_error_code_is_never_a_success(int code, BiometricOutcome expected)
    {
        var outcome = BiometricErrorMap.FromErrorCode(code);

        Assert.Equal(expected, outcome);
        Assert.NotEqual(BiometricOutcome.Succeeded, outcome);
    }
}
