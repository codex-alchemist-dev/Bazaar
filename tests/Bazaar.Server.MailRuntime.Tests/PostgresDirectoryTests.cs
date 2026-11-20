using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Mail;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.MailRuntime.Tests;

// Database tests: they skip on Windows without BAZAAR_TEST_PG and otherwise need a PostgreSQL (BAZAAR_TEST_PG or Docker).
public sealed class PostgresDirectoryTests
{
    private const string _skipReason = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(MailComponent).Assembly;
        const string marker = ".Sql.components.mail.";
        var files = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(marker, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var file = n[(n.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
                Assert.True(MigrationFile.TryParseFileName(file, out var version, out var name));
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return new MigrationFile("mail", version, name, reader.ReadToEnd());
            })
            .ToList();
        return new MigrationRunner(dataSource).MigrateAsync(new InMemoryMigrationSource(files), MigrateCommand.CurrentProvider(), ct);
    }

    private static async Task ExecAsync(NpgsqlDataSource ds, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task Address_directory_knows_open_mailboxes_only_and_ignores_case()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailrt", MigrateAsync, ct);
        var domain = Guid.NewGuid();
        await ExecAsync(db.DataSource, "INSERT INTO mail_domain (id, domain, backend_kind, state) VALUES ($1, 'ours.example', 'imap_smtp', 'active')", ct, domain);
        await ExecAsync(db.DataSource, "INSERT INTO mail_mailbox (id, domain_id, local_part) VALUES ($1, $2, 'agent')", ct, Guid.NewGuid(), domain);
        await ExecAsync(db.DataSource, "INSERT INTO mail_mailbox (id, domain_id, local_part, state) VALUES ($1, $2, 'gone', 'closed')", ct, Guid.NewGuid(), domain);
        var directory = new PostgresBazaarAddressDirectory(db.DataSource);

        Assert.True(await directory.IsBazaarAddressAsync("Agent@Ours.Example", ct));
        Assert.False(await directory.IsBazaarAddressAsync("gone@ours.example", ct));
        Assert.False(await directory.IsBazaarAddressAsync("agent@elsewhere.example", ct));
        Assert.False(await directory.IsBazaarAddressAsync("not-an-address", ct));
        Assert.False(await directory.IsBazaarAddressAsync("", ct));
    }

    [Fact]
    public async Task Domain_source_lists_active_domains_with_their_reference_only()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailrt", MigrateAsync, ct);
        await ExecAsync(db.DataSource, "INSERT INTO mail_domain (id, domain, backend_kind, config_ref, state) VALUES ($1, 'a.example', 'imap_smtp', 'mail.cfg.a', 'active')", ct, Guid.NewGuid());
        await ExecAsync(db.DataSource, "INSERT INTO mail_domain (id, domain, backend_kind, state) VALUES ($1, 'b.example', 'cloudflare', 'paused')", ct, Guid.NewGuid());
        await ExecAsync(db.DataSource, "INSERT INTO mail_domain (id, domain, backend_kind, state) VALUES ($1, 'c.example', 'bazaar_client', 'active')", ct, Guid.NewGuid());

        var rows = await new PostgresMailDomainSource(db.DataSource).ListActiveAsync(ct);

        Assert.Equal(
            [new MailDomainConfig("a.example", "imap_smtp", "mail.cfg.a"), new MailDomainConfig("c.example", "bazaar_client", null)],
            rows);
    }
}
