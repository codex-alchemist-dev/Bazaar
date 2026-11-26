namespace Bazaar.Client.Theme.Tests;

/// <summary>Reads a checked-in theme or golden file from the test output directory.</summary>
internal static class ThemeFixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name));
}
