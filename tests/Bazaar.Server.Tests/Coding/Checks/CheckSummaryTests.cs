using Bazaar.Server.Coding.Checks;

namespace Bazaar.Server.Tests.Coding.Checks;

/// <summary>Summary rules (doc 17, Verification): a pass needs a clean build, only allowed flags, and no count drop.</summary>
public sealed class CheckSummaryTests
{
    private static readonly IReadOnlySet<string> _allowNothing = new HashSet<string>();
    private static readonly IReadOnlySet<string> _allowSkip = new HashSet<string>([FlagCodes.SkipAdded]);
    private static readonly FlagFinding _skip = new(FlagCodes.SkipAdded, "tests/X/ThingTests.cs", "[Ignore(\"x\")]");
    private static readonly FlagFinding _countDrop = new(FlagCodes.TestCountDropped, string.Empty, "Test count dropped from 5 to 4 (tolerance 0).");

    [Fact]
    public void NullBuildExitCodeIsNotAPass()
    {
        Assert.False(CheckSummary.Summarize(null, [], null, _allowNothing));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(137)]
    public void NonzeroBuildExitCodeIsNotAPass(int exitCode)
    {
        Assert.False(CheckSummary.Summarize(exitCode, [], null, _allowNothing));
    }

    [Fact]
    public void ZeroExitWithNoFlagsAndNoCountFindingIsAPass()
    {
        Assert.True(CheckSummary.Summarize(0, [], null, _allowNothing));
    }

    [Fact]
    public void ZeroExitWithAllowedFlagIsAPass()
    {
        Assert.True(CheckSummary.Summarize(0, [_skip], null, _allowSkip));
    }

    [Fact]
    public void ZeroExitWithDisallowedFlagIsNotAPass()
    {
        Assert.False(CheckSummary.Summarize(0, [_skip], null, _allowNothing));
    }

    [Fact]
    public void ZeroExitWithCountFindingIsNotAPassEvenWhenNoFlags()
    {
        Assert.False(CheckSummary.Summarize(0, [], _countDrop, _allowNothing));
    }

    [Fact]
    public void ZeroExitWithAllowedFlagAndCountFindingIsNotAPass()
    {
        Assert.False(CheckSummary.Summarize(0, [_skip], _countDrop, _allowSkip));
    }
}
