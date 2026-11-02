using Bazaar.Server.Coding.Checks;

namespace Bazaar.Server.Tests.Coding.Checks;

/// <summary>Test-count gate (doc 17, Verification step 2): the head count may not drop below base minus tolerance.</summary>
public sealed class TestCountGateTests
{
    [Fact]
    public void EqualCountIsNotFlagged()
    {
        Assert.Null(TestCountGate.Evaluate(5, 5, 0));
    }

    [Fact]
    public void IncreasedCountIsNotFlagged()
    {
        Assert.Null(TestCountGate.Evaluate(5, 6, 0));
    }

    [Fact]
    public void DropWithNoToleranceIsFlagged()
    {
        var finding = TestCountGate.Evaluate(5, 4, 0);

        Assert.NotNull(finding);
        Assert.Equal(FlagCodes.TestCountDropped, finding.Code);
        Assert.Equal(string.Empty, finding.Path);
        Assert.Equal("Test count dropped from 5 to 4 (tolerance 0).", finding.Detail);
    }

    [Fact]
    public void DropExactlyAtToleranceIsNotFlagged()
    {
        Assert.Null(TestCountGate.Evaluate(10, 8, 2));
    }

    [Fact]
    public void DropOneBeyondToleranceIsFlagged()
    {
        Assert.NotNull(TestCountGate.Evaluate(10, 7, 2));
    }

    [Fact]
    public void NegativeToleranceThrows()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => TestCountGate.Evaluate(5, 5, -1));

        Assert.Equal("tolerance", ex.ParamName);
    }
}
