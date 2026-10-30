using System.IO;
using System.Linq;
using System.Reflection;

namespace Bazaar.Server.GitHub.Tests;

/// <summary>
/// Checks the embedded GitHub component migration (doc 21, work package 1). No live Postgres is needed: these tests
/// read the embedded resource and check its shape.
/// </summary>
public sealed class MigrationFileTests
{
    private const string MigrationSuffix = "0001_github_tables.sql";

    private static string ReadMigration(out string resourceName)
    {
        var assembly = typeof(GitHubComponent).Assembly;
        resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith(MigrationSuffix, System.StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Migration_resource_is_embedded_in_the_GitHub_assembly()
    {
        var names = typeof(GitHubComponent).Assembly.GetManifestResourceNames();

        Assert.Contains(names, name => name.EndsWith(MigrationSuffix, System.StringComparison.Ordinal));
    }

    [Fact]
    public void Migration_creates_the_four_github_tables()
    {
        var sql = ReadMigration(out _);

        Assert.Contains("CREATE TABLE github_app ", sql);
        Assert.Contains("CREATE TABLE github_pr ", sql);
        Assert.Contains("CREATE TABLE github_delivery ", sql);
        Assert.Contains("CREATE TABLE github_rate_state ", sql);
    }

    [Fact]
    public void Migration_keys_github_delivery_on_source_and_delivery_id()
    {
        var sql = ReadMigration(out _);

        Assert.Contains("PRIMARY KEY (source, delivery_id)", sql);
    }
}
