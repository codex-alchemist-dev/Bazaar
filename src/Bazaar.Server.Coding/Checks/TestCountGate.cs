namespace Bazaar.Server.Coding.Checks;

/// <summary>Test count must not drop against the base branch (doc 17, Verification step 2). Counts come from the caller.</summary>
public static class TestCountGate
{
    /// <summary>Returns a finding when <paramref name="headCount"/> is below <paramref name="baseCount"/> minus <paramref name="tolerance"/>; otherwise null.</summary>
    public static FlagFinding? Evaluate(int baseCount, int headCount, int tolerance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tolerance);
        return headCount < baseCount - tolerance
            ? new FlagFinding(FlagCodes.TestCountDropped, string.Empty, $"Test count dropped from {baseCount} to {headCount} (tolerance {tolerance}).")
            : null;
    }
}
