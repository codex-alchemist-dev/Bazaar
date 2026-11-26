using Bazaar.GitHub.Tools;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.GitHub.Tests.Tools;

/// <summary><see cref="PgGitHubAgentInstallations"/> against a real <c>github_app</c> table.</summary>
public sealed class PgGitHubAgentInstallationsTests
{
    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(GitHubComponent).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0001_github_tables.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AddAppAsync(NpgsqlDataSource ds, Guid agent, long installation, string status, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("INSERT INTO github_app (agent_id, app_id, installation_id, account_login, status, key_ref) VALUES ($1, 1, $2, 'acme', $3, 'ref')");
        cmd.Parameters.AddWithValue(agent);
        cmd.Parameters.AddWithValue(installation);
        cmd.Parameters.AddWithValue(status);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task Only_an_active_row_resolves_and_unknown_agents_get_none()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghagentinst", MigrateAsync, ct);
        var active = Guid.NewGuid();
        var suspended = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        await AddAppAsync(db.DataSource, active, 101, "active", ct);
        await AddAppAsync(db.DataSource, suspended, 102, "suspended", ct);
        await AddAppAsync(db.DataSource, revoked, 103, "revoked", ct);
        var installs = new PgGitHubAgentInstallations(db.DataSource);

        Assert.Equal(101, await installs.GetInstallationIdAsync(active, ct));
        Assert.Null(await installs.GetInstallationIdAsync(suspended, ct));
        Assert.Null(await installs.GetInstallationIdAsync(revoked, ct));
        Assert.Null(await installs.GetInstallationIdAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task One_agents_row_is_never_returned_for_another_agent()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghagentinst2", MigrateAsync, ct);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await AddAppAsync(db.DataSource, a, 201, "active", ct);
        await AddAppAsync(db.DataSource, b, 202, "active", ct);
        var installs = new PgGitHubAgentInstallations(db.DataSource);

        Assert.Equal(201, await installs.GetInstallationIdAsync(a, ct));
        Assert.Equal(202, await installs.GetInstallationIdAsync(b, ct));
    }
}
