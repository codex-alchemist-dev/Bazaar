namespace Bazaar.Architecture.Tests;

/// <summary>
/// The VM start that skips the containment check (<c>StartForReleaseAsync</c>) exists only for the owner-signed release path.
/// It must stay non-public, and only the pauser that the release flow drives may call it, so no other caller can skip containment.
/// </summary>
public class ContainmentGateTests
{
    private const string _method = "StartForReleaseAsync";
    private static readonly string[] _expected = ["VmContainmentPauser.cs", "VmLifecycleService.cs"];

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void The_release_start_is_not_public()
    {
        var declaration = SourceFiles()
            .SelectMany(File.ReadAllLines)
            .Single(l => l.Contains($"Task<VmOperationResult> {_method}", StringComparison.Ordinal));
        Assert.DoesNotContain("public ", declaration, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_containment_pauser_calls_the_release_start()
    {
        var callers = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains(_method, StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(_expected, callers);
    }
}
