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
    public async Task Close_needs_the_owning_agent_is_idempotent_and_keeps_person_id()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("onboarding_close", MigrateAsync, ct);
        var store = new PostgresMailboxPersonStore(db.DataSource);
        var agent = Guid.NewGuid();
        var box = await MailboxAsync(db.DataSource, agent, ct);
        var unowned = await MailboxAsync(db.DataSource, null, ct);

        Assert.Equal(MailboxCloseResult.NotOwner, await store.CloseAsync(box, Guid.NewGuid(), ct));
        Assert.Equal(MailboxCloseResult.NotOwner, await store.CloseAsync(unowned, agent, ct));
        Assert.Equal(MailboxCloseResult.NotFound, await store.CloseAsync(Guid.NewGuid(), agent, ct));
        Assert.Equal("active", await StateAsync(db.DataSource, box, ct));

        Assert.Equal(MailboxCloseResult.Closed, await store.CloseAsync(box, agent, ct));
        Assert.Equal(MailboxCloseResult.AlreadyClosed, await store.CloseAsync(box, agent, ct));
        Assert.Equal("closed", await StateAsync(db.DataSource, box, ct));
        Assert.Equal(new MailboxOwner(true, agent), await store.GetOwnerAsync(box, ct));
    }

    [Fact]
    public async Task Owner_directory_reads_the_one_active_owner_and_ignores_others()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("onboarding_owner", MigrateAsync, ct);
        var directory = new PostgresMailOwnerDirectory(db.DataSource);
        // The human table belongs to the remote component (remote/0001); only the columns the directory reads are created here.
        await using (var ddl = db.DataSource.CreateCommand("CREATE TABLE human (id uuid PRIMARY KEY, display_name text NOT NULL, role text NOT NULL, status text NOT NULL)"))
        {
            await ddl.ExecuteNonQueryAsync(ct);
        }

        Assert.Null(await directory.GetOwnerAsync(ct));

        var owner = Guid.NewGuid();
        await InsertHumanAsync(db.DataSource, Guid.NewGuid(), "Coworker", "coworker", "active", ct);
        await InsertHumanAsync(db.DataSource, owner, "The Owner", "owner", "suspended", ct);
        Assert.Null(await directory.GetOwnerAsync(ct));

        await using (var activate = db.DataSource.CreateCommand("UPDATE human SET status = 'active' WHERE role = 'owner'"))
        {
            await activate.ExecuteNonQueryAsync(ct);
        }

        Assert.Equal(new MailOwner(owner, "The Owner"), await directory.GetOwnerAsync(ct));
    }

    private static async Task InsertHumanAsync(NpgsqlDataSource ds, Guid id, string name, string role, string status, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("INSERT INTO human (id, display_name, role, status) VALUES ($1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { Value = role });
        cmd.Parameters.Add(new NpgsqlParameter { Value = status });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> StateAsync(NpgsqlDataSource ds, Guid mailbox, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT state FROM mail_mailbox WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = mailbox });
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
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
