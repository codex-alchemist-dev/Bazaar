using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Mail;
using Bazaar.Server.MailInbound;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.MailRuntime.Tests;

// Database tests follow the sibling tests: they skip on Windows without BAZAAR_TEST_PG and otherwise need a PostgreSQL (BAZAAR_TEST_PG or Docker).
// Only the GC columns the directory reads are stubbed.
public sealed class PostgresInboundMailboxDirectoryTests
{
    private const string _skipReason = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    private sealed class StubBackend : IMailBackend
    {
        public Task<IReadOnlyList<RawMail>> ReceiveAsync(MailboxRef box, CancellationToken ct) => Task.FromResult<IReadOnlyList<RawMail>>([]);

        public Task<SendReceipt> SendAsync(OutboundMail mail, CancellationToken ct) => throw new NotSupportedException();

        public Task<BackendHealth> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MapCatalog(params string[] domains) : IMailBackendCatalog
    {
        private readonly StubBackend _backend = new();

        public IMailBackend? Resolve(string domain) => domains.Contains(domain) ? _backend : null;

        public Task<MailBackendRefreshResult> RefreshAsync(CancellationToken ct) => Task.FromResult(new MailBackendRefreshResult(domains.Length, 0, []));
    }

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

        // The GC tables belong to another module; the directory reads these columns only.
        await ExecAsync(dataSource, "CREATE TABLE gc (gc_id uuid PRIMARY KEY, type_key text NOT NULL, archived_at timestamptz)", ct);
        await ExecAsync(dataSource, "CREATE TABLE gc_member (gc_id uuid NOT NULL, principal_id uuid NOT NULL, left_seq bigint, PRIMARY KEY (gc_id, principal_id))", ct);
    }

    private static async Task ExecAsync(NpgsqlDataSource ds, string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a ?? DBNull.Value });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Guid> DomainAsync(NpgsqlDataSource ds, string name, string state, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecAsync(ds, "INSERT INTO mail_domain (id, domain, backend_kind, state) VALUES ($1, $2, 'imap_smtp', $3)", ct, id, name, state);
        return id;
    }

    private static async Task<Guid> MailboxAsync(NpgsqlDataSource ds, Guid domain, string local, Guid? person, string state, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await ExecAsync(ds, "INSERT INTO mail_mailbox (id, domain_id, local_part, person_id, state) VALUES ($1, $2, $3, $4, $5)", ct, id, domain, local, person, state);
        return id;
    }

    private static async Task<Guid> InboxAsync(NpgsqlDataSource ds, Guid member, string type, bool archived, bool left, CancellationToken ct)
    {
        var gc = Guid.NewGuid();
        await ExecAsync(ds, "INSERT INTO gc (gc_id, type_key, archived_at) VALUES ($1, $2, CASE WHEN $3 THEN now() END)", ct, gc, type, archived);
        await ExecAsync(ds, "INSERT INTO gc_member (gc_id, principal_id, left_seq) VALUES ($1, $2, CASE WHEN $3 THEN 5::bigint END)", ct, gc, member, left);
        return gc;
    }

    [Fact]
    public async Task Maps_an_active_mailbox_to_its_person_and_open_inbox_gc()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailbox", MigrateAsync, ct);
        var person = Guid.NewGuid();
        var domain = await DomainAsync(db.DataSource, "ours.example", "active", ct);
        var mailbox = await MailboxAsync(db.DataSource, domain, "agent", person, "active", ct);
        var gc = await InboxAsync(db.DataSource, person, "inbox", archived: false, left: false, ct);
        var directory = new PostgresInboundMailboxDirectory(db.DataSource, new MapCatalog("ours.example"));

        var listed = Assert.Single(await directory.ListActiveAsync(ct));

        Assert.Equal(mailbox, listed.MailboxId);
        Assert.Equal(person, listed.PersonId);
        Assert.Equal(gc, listed.InboxGcId);
        Assert.Equal(new MailboxRef("ours.example", "agent"), listed.Box);
        Assert.Equal(0, directory.LastSkipped);
    }

    [Fact]
    public async Task Skips_mailboxes_it_cannot_fully_map_and_counts_them()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailbox", MigrateAsync, ct);
        var ds = db.DataSource;
        var active = await DomainAsync(ds, "ours.example", "active", ct);
        var paused = await DomainAsync(ds, "paused.example", "paused", ct);
        var nobackend = await DomainAsync(ds, "nobackend.example", "active", ct);

        // Not selected at all: no person, paused domain, closed mailbox. Skipped and counted: no inbox GC, two, wrong type, archived, person left, no back end.
        await MailboxAsync(ds, active, "noperson", null, "active", ct);
        await MailboxAsync(ds, active, "nogc", Guid.NewGuid(), "active", ct);
        var two = Guid.NewGuid();
        await MailboxAsync(ds, active, "two", two, "active", ct);
        await InboxAsync(ds, two, "inbox", false, false, ct);
        await InboxAsync(ds, two, "inbox", false, false, ct);
        var wrong = Guid.NewGuid();
        await MailboxAsync(ds, active, "wrong", wrong, "active", ct);
        await InboxAsync(ds, wrong, "dm", false, false, ct);
        var archived = Guid.NewGuid();
        await MailboxAsync(ds, active, "archived", archived, "active", ct);
        await InboxAsync(ds, archived, "inbox", true, false, ct);
        var left = Guid.NewGuid();
        await MailboxAsync(ds, active, "left", left, "active", ct);
        await InboxAsync(ds, left, "inbox", false, true, ct);
        var nb = Guid.NewGuid();
        await MailboxAsync(ds, nobackend, "agent", nb, "active", ct);
        await InboxAsync(ds, nb, "inbox", false, false, ct);
        var pd = Guid.NewGuid();
        await MailboxAsync(ds, paused, "agent", pd, "active", ct);
        await InboxAsync(ds, pd, "inbox", false, false, ct);
        var closed = Guid.NewGuid();
        await MailboxAsync(ds, active, "closed", closed, "closed", ct);
        await InboxAsync(ds, closed, "inbox", false, false, ct);
        var good = Guid.NewGuid();
        await MailboxAsync(ds, active, "good", good, "active", ct);
        await InboxAsync(ds, good, "inbox", false, false, ct);

        var directory = new PostgresInboundMailboxDirectory(ds, new MapCatalog("ours.example"));
        var listed = await directory.ListActiveAsync(ct);

        Assert.Equal(good, Assert.Single(listed).PersonId);
        Assert.Equal(6, directory.LastSkipped);
    }

    [Fact]
    public void Runtime_registers_the_directory_in_place_of_the_fail_closed_default()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NpgsqlDataSourceBuilder("Host=localhost").Build());
        services.AddSingleton<IMailGcAppender>(new RecordingAppender());
        services.AddBazaarMailRuntime();
        using var sp = services.BuildServiceProvider();

        Assert.IsType<PostgresInboundMailboxDirectory>(sp.GetRequiredService<IInboundMailboxDirectory>());
    }
}
