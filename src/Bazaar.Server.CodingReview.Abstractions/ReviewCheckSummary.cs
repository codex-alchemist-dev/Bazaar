namespace Bazaar.Server.CodingReview;

/// <summary>
/// Stable flag codes from the server-side check runner (doc 17, Verification). They mirror the server's codes as data, so this
/// module does not reference the server project. A code the server adds later reads as a plain check flag.
/// </summary>
public static class ReviewCheckFlagCodes
{
    /// <summary>A new Skip or Ignore attribute was added to a test.</summary>
    public const string SkipAdded = "skip-added";

    /// <summary>An <c>#if false</c> block was added.</summary>
    public const string IfFalseAdded = "if-false-added";

    /// <summary>A test attribute was removed.</summary>
    public const string TestRemoved = "test-removed";

    /// <summary>An assertion call was removed.</summary>
    public const string AssertionRemoved = "assertion-removed";

    /// <summary>A test file changed.</summary>
    public const string TestFileChanged = "test-file-changed";

    /// <summary>The test count dropped against the base branch.</summary>
    public const string TestCountDropped = "test-count-dropped";

    /// <summary>The codes that mean a check may have been faked. They are surfaced at the top of every review.</summary>
    public static readonly IReadOnlySet<string> FakeSuccess = new HashSet<string>(StringComparer.Ordinal)
    {
        SkipAdded,
        IfFalseAdded,
        TestRemoved,
        AssertionRemoved,
        TestCountDropped,
    };
}

/// <summary>One flag from the check runner. <see cref="Path"/> is null for repo-wide findings such as a test count drop.</summary>
public sealed record ReviewCheckFlag(string Code, string? Path, UntrustedText Detail);

/// <summary>
/// The server-side check result for one commit: a single pass or fail, the build exit code and the flags. Doc 17, Verification.
/// </summary>
public sealed record ReviewCheckSummary(bool Passed, int? BuildExitCode, IReadOnlyList<ReviewCheckFlag> Flags);
