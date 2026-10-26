namespace Bazaar.Client.Core.Tests.Theme;

/// <summary>Compares generated text with a checked-in golden file under Theme/Golden; set BAZAAR_UPDATE_GOLDEN=1 to rewrite them.</summary>
internal static class GoldenFile
{
    public static void Check(string name, string actual)
    {
        string normalised = Normalise(actual);
        if (Environment.GetEnvironmentVariable("BAZAAR_UPDATE_GOLDEN") == "1")
        {
            string source = Path.Combine(FindTestsDir(), "Theme", "Golden", name);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, normalised + "\n");
            return;
        }

        string expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Theme", "Golden", name));
        Assert.Equal(Normalise(expected), normalised);
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

    private static string FindTestsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("*.csproj").Length == 0) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("test project directory not found");
    }
}
