using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Mail;
using Bazaar.Server.MailOutbound;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.MailRuntime.Tests;

// Database tests: they skip on Windows without BAZAAR_TEST_PG and otherwise need a PostgreSQL (BAZAAR_TEST_PG or Docker).
public sealed class PostgresApprovedRecipientsTests
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

    private static ApprovedRecipientEntry Address(string value) => new(ApprovedRecipientKind.Address, value, false, "test", null);

    private static ApprovedRecipientEntry Domain(string value, bool subdomains = false) => new(ApprovedRecipientKind.Domain, value, subdomains, "test", null);

    [Fact]
    public async Task Nothing_is_approved_on_an_empty_list()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailrt", MigrateAsync, ct);
        var recipients = new StoreApprovedRecipients(new PostgresApprovedRecipientStore(db.DataSource));

        Assert.False(await recipients.IsApprovedAsync("agent", "someone@partner.example", ct));
    }

    [Fact]
    public async Task Address_entries_match_exactly_and_ignore_case()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailrt", MigrateAsync, ct);
        var store = new PostgresApprovedRecipientStore(db.DataSource);
        Assert.True(await store.AddAsync(Address("  Pat@Partner.Example "), ct));

        Assert.True(await store.IsApprovedAsync("pat@partner.example", ct));
        Assert.True(await store.IsApprovedAsync("PAT@PARTNER.EXAMPLE", ct));
        Assert.False(await store.IsApprovedAsync("other@partner.example", ct));
        Assert.False(await store.IsApprovedAsync("pat@sub.partner.example", ct));
    }

    [Fact]
    public async Task Domain_entries_match_the_domain_and_subdomains_only_when_marked()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailrt", MigrateAsync, ct);
        var store = new PostgresApprovedRecipientStore(db.DataSource);
        await store.AddAsync(Domain("Exact.Example"), ct);
        await store.AddAsync(Domain("wide.example", subdomains: true), ct);

        Assert.True(await store.IsApprovedAsync("a@exact.example", ct));
        Assert.False(await store.IsApprovedAsync("a@mail.exact.example", ct));
        Assert.True(await store.IsApprovedAsync("a@wide.example", ct));
        Assert.True(await store.IsApprovedAsync("a@Mail.Wide.Example", ct));
        Assert.True(await store.IsApprovedAsync("a@x.y.wide.example", ct));
        Assert.False(await store.IsApprovedAsync("a@notwide.example", ct));
        Assert.False(await store.IsApprovedAsync("a@wide.example.evil.test", ct));
    }

    [Fact]
    public async Task Malformed_addresses_are_never_approved_and_malformed_entries_are_refused()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailrt", MigrateAsync, ct);
        var store = new PostgresApprovedRecipientStore(db.DataSource);
        await store.AddAsync(Domain("wide.example", subdomains: true), ct);

        Assert.False(await store.IsApprovedAsync("", ct));
        Assert.False(await store.IsApprovedAsync("wide.example", ct));
        Assert.False(await store.IsApprovedAsync("a@@wide.example", ct));
        Assert.False(await store.IsApprovedAsync("a b@wide.example", ct));
        Assert.False(await store.IsApprovedAsync("a@", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(Address("not-an-address"), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(Domain("*.example"), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(Domain("a@b.example"), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(Address("a@b.example") with { IncludeSubdomains = true }, ct));
    }

    [Fact]
    public async Task Add_is_idempotent_and_remove_takes_effect_at_once()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailrt", MigrateAsync, ct);
        var store = new PostgresApprovedRecipientStore(db.DataSource);
        var approval = Guid.NewGuid();

        Assert.True(await store.AddAsync(Domain("partner.example") with { ApprovalId = approval }, ct));
        Assert.False(await store.AddAsync(Domain("PARTNER.example"), ct));
        var rows = await store.ListAsync(ct);
        var row = Assert.Single(rows);
        Assert.Equal(approval, row.ApprovalId);
        Assert.True(await store.IsApprovedAsync("a@partner.example", ct));

        Assert.True(await store.RemoveAsync(ApprovedRecipientKind.Domain, "Partner.Example", ct));
        Assert.False(await store.RemoveAsync(ApprovedRecipientKind.Domain, "partner.example", ct));
        Assert.False(await store.IsApprovedAsync("a@partner.example", ct));
    }

    [Fact]
    public async Task Schema_refuses_values_that_are_not_lower_case()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailrt", MigrateAsync, ct);
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO mail_approved_recipient (id, kind, value, added_by) VALUES ($1, 'domain', 'Upper.Example', 'x')");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });

        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(ct));
    }

    [Fact]
    public async Task Registration_in_front_of_the_runtime_replaces_the_fail_closed_default()
    {
        var services = new ServiceCollection();
        var store = new FakeStore();
        services.TryAddSingleton<IApprovedRecipients>(new StoreApprovedRecipients(store));
        services.TryAddSingleton<IApprovedRecipients, FailClosedApprovedRecipients>();
        await using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IApprovedRecipients>();

        Assert.IsType<StoreApprovedRecipients>(resolved);
        Assert.True(await resolved.IsApprovedAsync("agent", "a@b.example", TestContext.Current.CancellationToken));
    }

    private sealed class FakeStore : IApprovedRecipientStore
    {
        public Task<bool> AddAsync(ApprovedRecipientEntry entry, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> RemoveAsync(ApprovedRecipientKind kind, string value, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyList<ApprovedRecipientEntry>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ApprovedRecipientEntry>>([]);

        public Task<bool> IsApprovedAsync(string address, CancellationToken ct) => Task.FromResult(true);
    }
}
