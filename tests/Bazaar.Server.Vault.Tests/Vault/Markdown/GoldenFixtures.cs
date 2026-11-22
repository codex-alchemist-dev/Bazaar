namespace Bazaar.Server.Tests.Vault.Markdown;

/// <summary>Locates the Vault Markdown golden inputs (<c>&lt;name&gt;.md</c> with <c>&lt;name&gt;.links.json</c>) in the test output.</summary>
internal static class GoldenFixtures
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "Vault", "Markdown", "Golden");

    public static string Document(string name) => Path.Combine(Directory, name + ".md");

    public static string ExpectedLinks(string name) => Path.Combine(Directory, name + ".links.json");

    /// <summary>One row per golden <c>.md</c> file, so a new fixture is covered without editing the tests.</summary>
    public static IEnumerable<object[]> Names() =>
        System.IO.Directory.GetFiles(Directory, "*.md")
            .Select(path => (object[])[Path.GetFileNameWithoutExtension(path)])
            .OrderBy(row => (string)row[0]);
}
