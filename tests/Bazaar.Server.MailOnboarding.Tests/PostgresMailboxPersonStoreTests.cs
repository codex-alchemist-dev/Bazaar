using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Mail;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.MailOnboarding.Tests;

// Database tests follow the sibling tests: they skip on Windows without BAZAAR_TEST_PG and otherwise need a PostgreSQL (BAZAAR_TEST_PG or Docker).
public sealed class PostgresMailboxPersonStoreTests
{
    private const string _skipReason = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
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
        await new MigrationRunner(dataSource).MigrateAsync(new InMemoryMigrationSource(files), MigrateCommand.CurrentProvider(), ct);
    }

    private static async Task<Guid> MailboxAsync(NpgsqlDataSource ds, Guid? person, CancellationToken ct)
    {
        var domain = Guid.NewGuid();
        await using (var cmd = ds.CreateCommand("INSERT INTO mail_domain (id, domain, backend_kind, state) VALUES ($1, $2, 'imap_smtp', 'active')"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = domain });
            cmd.Parameters.Add(new NpgsqlParameter { Value = $"{domain:N}.example" });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var id = Guid.NewGuid();
        await using (var cmd = ds.CreateCommand("INSERT INTO mail_mailbox (id, domain_id, local_part, person_id, state) VALUES ($1, $2, 'agent', $3, 'active')"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = domain });
            cmd.Parameters.Add(new NpgsqlParameter { Value = person is null ? DBNull.Value : person });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    [Fact]
    public async Task Sets_once_never_reassigns_and_reports_missing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("onboarding", MigrateAsync, ct);
        var store = new PostgresMailboxPersonStore(db.DataSource);
        var box = await MailboxAsync(db.DataSource, null, ct);
        var agent = Guid.NewGuid();

        Assert.Equal(MailboxPersonSetResult.Set, await store.SetPersonAsync(box, agent, ct));
        Assert.Equal(MailboxPersonSetResult.AlreadySet, await store.SetPersonAsync(box, agent, ct));
        Assert.Equal(MailboxPersonSetResult.OwnedByOther, await store.SetPersonAsync(box, Guid.NewGuid(), ct));
        Assert.Equal(new MailboxOwner(true, agent), await store.GetOwnerAsync(box, ct));
        Assert.Equal(MailboxPersonSetResult.NotFound, await store.SetPersonAsync(Guid.NewGuid(), agent, ct));
        Assert.Equal(new MailboxOwner(false, null), await store.GetOwnerAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task Racing_agents_leave_exactly_one_owner()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("onboarding_race", MigrateAsync, ct);
        var store = new PostgresMailboxPersonStore(db.DataSource);
        var box = await MailboxAsync(db.DataSource, null, ct);
        var agents = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();

        var results = await Task.WhenAll(agents.Select(a => Task.Run(() => store.SetPersonAsync(box, a, ct), ct)));

        Assert.Equal(1, results.Count(r => r == MailboxPersonSetResult.Set));
        Assert.Equal(5, results.Count(r => r == MailboxPersonSetResult.OwnedByOther));
        var owner = (await store.GetOwnerAsync(box, ct)).PersonId;
        Assert.Contains(owner!.Value, agents);
    }
}
