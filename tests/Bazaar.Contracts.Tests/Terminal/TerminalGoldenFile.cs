namespace Bazaar.Contracts.Tests.Terminal;

/// <summary>Compares text with a checked-in golden file; set BAZAAR_UPDATE_GOLDEN=1 to rewrite them.</summary>
internal static class GoldenFile
{
    public static void Check(string name, string actual)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Terminal", "Golden", name);
        if (Environment.GetEnvironmentVariable("BAZAAR_UPDATE_GOLDEN") == "1")
        {
            string source = Path.Combine(FindTestsDir(), "Terminal", "Golden", name);
            File.WriteAllText(source, actual + "\n");
            return;
        }

        Assert.Equal(File.ReadAllText(path).TrimEnd('\n'), actual);
    }

    private static string FindTestsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("*.csproj").Length == 0) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("test project directory not found");
    }
}
