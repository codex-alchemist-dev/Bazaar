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
    private const string _migrationSuffix = "0001_github_tables.sql";

    private static string ReadMigration(out string resourceName)
    {
        var assembly = typeof(GitHubComponent).Assembly;
        resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith(_migrationSuffix, System.StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Migration_resource_is_embedded_in_the_GitHub_assembly()
    {
        var names = typeof(GitHubComponent).Assembly.GetManifestResourceNames();

        Assert.Contains(names, name => name.EndsWith(_migrationSuffix, System.StringComparison.Ordinal));
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

    [Fact]
    public void Payload_migration_is_embedded_and_idempotent()
    {
        var assembly = typeof(GitHubComponent).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0002_github_delivery_payload.sql", System.StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        var sql = reader.ReadToEnd();

        Assert.Contains("ADD COLUMN IF NOT EXISTS payload jsonb", sql);
        Assert.Contains("ADD COLUMN IF NOT EXISTS event_type text", sql);
    }

    [Fact]
    public void Processed_migration_is_embedded_and_adds_the_processor_columns_and_partial_index()
    {
        var assembly = typeof(GitHubComponent).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0003_github_delivery_processed.sql", System.StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        var sql = reader.ReadToEnd();

        Assert.Contains("processed_at timestamptz", sql);
        Assert.Contains("process_error text", sql);
        Assert.Contains("WHERE processed_at IS NULL", sql);
    }
}
