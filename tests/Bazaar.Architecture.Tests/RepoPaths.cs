namespace Bazaar.Architecture.Tests;

/// <summary>Locates the repository root from the test binaries, so source scans work from any build output.</summary>
internal static class RepoPaths
{
    private const string _solutionFile = "Bazaar.slnx";

    public static string Root { get; } = FindRoot(AppContext.BaseDirectory);

    public static string SourceDirectory(string project) => Path.Combine(Root, "src", project);

    public static string ProjectFile(string project) => Path.Combine(SourceDirectory(project), project + ".csproj");

    internal static string FindRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, _solutionFile)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException($"{_solutionFile} not found above {start}.");
    }
}
