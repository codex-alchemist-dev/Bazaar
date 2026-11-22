namespace Bazaar.Server.Coding.Checks;

/// <summary>One flag from a pure check. <see cref="Path"/> is empty for repo-wide findings such as a test count drop.</summary>
public sealed record FlagFinding(string Code, string Path, string Detail);

/// <summary>Stable codes for check flags (doc 17, Verification).</summary>
public static class FlagCodes
{
    public const string SkipAdded = "skip-added";
    public const string IfFalseAdded = "if-false-added";
    public const string TestRemoved = "test-removed";
    public const string AssertionRemoved = "assertion-removed";
    public const string TestFileChanged = "test-file-changed";
    public const string TestCountDropped = "test-count-dropped";
}
