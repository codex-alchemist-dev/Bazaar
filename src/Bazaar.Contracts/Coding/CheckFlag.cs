namespace Bazaar.Contracts.Coding;

/// <summary>
/// Codes for suspicious changes that a check run reports against its base commit
/// (docs/design/17-coding-agent-harness.md, Verification). The codes are wire values, so they stay stable.
/// </summary>
public static class CheckFlag
{
    /// <summary>A skip marker was added to a test.</summary>
    public const string SkipAdded = "skip-added";

    /// <summary>An if-false guard was added to a test.</summary>
    public const string IfFalseAdded = "if-false-added";

    /// <summary>A test was removed.</summary>
    public const string TestRemoved = "test-removed";

    /// <summary>An assertion was removed.</summary>
    public const string AssertionRemoved = "assertion-removed";

    /// <summary>A test file changed.</summary>
    public const string TestFileChanged = "test-file-changed";

    /// <summary>The test count fell below the base commit's count.</summary>
    public const string TestCountDropped = "test-count-dropped";
}
