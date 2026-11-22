namespace Bazaar.Rubedo.Docs.Tests;

/// <summary>Finds the repository root by walking up from the test binary to the folder that holds <c>Bazaar.slnx</c>.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string CommittedReference => Path.Combine(Root, "docs", "reference", "rubedo");

    public static string GoldenCorpus => Path.Combine(Root, "tests", "Bazaar.Rubedo.Tests", "Golden", "rubedo-v1.json");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bazaar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Bazaar.slnx was not found above the test binary.");
    }
}
