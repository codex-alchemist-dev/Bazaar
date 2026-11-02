namespace Bazaar.Server.Coding.Checks;

/// <summary>
/// Flags every changed test file (doc 17, Verification step 3). Adding a test is still a change to the test suite,
/// so it needs a named reviewer like an edit does.
/// </summary>
public static class TestFileChangeFlagger
{
    public static IReadOnlyList<FlagFinding> Flag(IReadOnlyList<ChangedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files
            .Where(f => IsTestPath(f.Path))
            .Select(f => new FlagFinding(FlagCodes.TestFileChanged, f.Path, $"{f.Kind} test file: {f.Path}"))
            .ToList();
    }

    /// <summary>True for a path under a <c>tests/</c> folder, or a file named <c>*Tests.cs</c> or <c>*Test.cs</c>.</summary>
    internal static bool IsTestPath(string path) =>
        IsUnderTestsFolder(path) || FileName(path).EndsWith("Tests.cs", StringComparison.Ordinal) || FileName(path).EndsWith("Test.cs", StringComparison.Ordinal);

    /// <summary>True when any folder segment (not the file name) is <c>tests</c>. Paths use <c>/</c>, as git reports them.</summary>
    internal static bool IsUnderTestsFolder(string path) =>
        path.Split('/').SkipLast(1).Any(segment => segment.Equals("tests", StringComparison.Ordinal));

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
}
