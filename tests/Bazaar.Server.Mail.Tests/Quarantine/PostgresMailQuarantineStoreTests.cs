using System.Reflection;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Mail.Quarantine;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Mail.Tests.Quarantine;

// Database tests: they skip on Windows without BAZAAR_TEST_PG and otherwise need a PostgreSQL (BAZAAR_TEST_PG or Docker).
public sealed class PostgresMailQuarantineStoreTests
{
    private static readonly Guid _owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _person = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly QuarantineTransitionContext _none = new();
    private const string _skipReason = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Applies the mail namespace (0021, 0022) read from the module assembly, the same files the host's component source reads.</summary>
    private static Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(MailComponent).Assembly;
        var files = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Sql.components.mail.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n =>
            {
                var file = n[(n.IndexOf(".Sql.components.mail.", StringComparison.Ordinal) + ".Sql.components.mail.".Length)..];
                Assert.True(MigrationFile.TryParseFileName(file, out var version, out var name));
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return new MigrationFile("mail", version, name, reader.ReadToEnd());
            })
            .ToList();
        return new MigrationRunner(dataSource).MigrateAsync(new InMemoryMigrationSource(files), MigrateCommand.CurrentProvider(), ct);
    }

    private static Task<PgTestDatabase> CreateDbAsync(CancellationToken ct) => PgTestDatabase.CreateAsync("mailq", MigrateAsync, ct);

    private static async Task<Guid> MailboxAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        var domain = Guid.NewGuid();
        var mailbox = Guid.NewGuid();
        await Exec(ds, "INSERT INTO mail_domain (id, domain, backend_kind) VALUES ($1, $2, 'imap_smtp')", [domain, $"{domain:N}.example"], ct);
        await Exec(ds, "INSERT INTO mail_mailbox (id, domain_id, local_part) VALUES ($1, $2, 'agent')", [mailbox, domain], ct);
        return mailbox;
    }

    /// <summary>An inbound message as the pipeline stores it before quarantine: state received, body encrypted, full address in from_addr.</summary>
    private static async Task<Guid> InsertReceivedAsync(NpgsqlDataSource ds, Guid mailbox, CancellationToken ct, string state = "received")
    {
        var id = Guid.NewGuid();
        await Exec(ds,
            "INSERT INTO mail_message (id, mailbox_id, direction, from_addr, state, body_key_id, body_ct, body_nonce, received_at) " +
            "VALUES ($1, $2, 'inbound', 'alice@Spam.Example', $3, $4, $5, $6, $7)",
            [id, mailbox, state, Guid.NewGuid(), new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }, _t0], ct);
        return id;
    }

    private static QuarantineItem Item(Guid id, string domain = "spam.example", DateTimeOffset? received = null, Guid? person = null, DateTimeOffset? until = null) =>
        new(id, person ?? _person, domain, ["AuthenticationFailed", "UnknownSender"], "spf=fail dkim=fail", received ?? _t0, QuarantineState.Held, until);

    private static async Task Exec(NpgsqlDataSource ds, string sql, object[] args, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource ds, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        var value = (await cmd.ExecuteScalarAsync(ct))!;
        // An untyped timestamptz comes back as a UTC DateTime.
        return value is DateTime utc && typeof(T) == typeof(DateTimeOffset) ? (T)(object)new DateTimeOffset(utc) : (T)value;
    }

    private static async Task<Guid> AdmitNewAsync(PostgresMailQuarantineStore store, NpgsqlDataSource ds, Guid mailbox, CancellationToken ct, string domain = "spam.example")
    {
        var id = await InsertReceivedAsync(ds, mailbox, ct);
        var admit = await store.AdmitAsync(Item(id, domain), QuarantineActor.Reader, ct);
        Assert.True(admit.IsAllowed);
        return id;
    }

    [Fact]
    public void The_store_never_selects_a_body_or_a_full_address()
    {
        // Every SQL constant in the store, checked as text: the quarantine path may not read body or from_addr at all.
        var sql = typeof(PostgresMailQuarantineStore)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(sql);
        foreach (var text in sql.Where(t => t.StartsWith("SELECT", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("body_", text, StringComparison.Ordinal);
            Assert.DoesNotContain("from_addr", text, StringComparison.Ordinal);
            Assert.DoesNotContain("blob_ref", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Admit_holds_a_received_message_and_stores_domain_only()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        var store = new PostgresMailQuarantineStore(db.DataSource);
        var id = await InsertReceivedAsync(db.DataSource, await MailboxAsync(db.DataSource, ct), ct);

        var admit = await store.AdmitAsync(Item(id, "Alice@Spam.Example", until: _t0.AddDays(30)), QuarantineActor.System, ct);

        Assert.True(admit.IsAllowed);
        var got = (await store.GetAsync(id, ct))!;
        Assert.Equal(QuarantineState.Held, got.State);
        Assert.Equal("spam.example", got.SenderDomain);
        Assert.Equal(_person, got.PersonId);
        Assert.Equal(["AuthenticationFailed", "UnknownSender"], got.ReasonCodes);
        Assert.Equal("spf=fail dkim=fail", got.AuthResult);
        Assert.Equal(_t0, got.ReceivedAt);
        Assert.Equal(_t0.AddDays(30), got.RetentionUntil);
        Assert.True(got.Tainted);
        Assert.Equal(1, await store.CountHeldAsync(ct));
    }

    [Fact]
    public async Task Admit_refuses_agents_unknown_ids_and_a_second_admit_of_a_moved_on_item()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        var store = new PostgresMailQuarantineStore(db.DataSource);
        var mailbox = await MailboxAsync(db.DataSource, ct);
        var fresh = await InsertReceivedAsync(db.DataSource, mailbox, ct);

        Assert.Equal(QuarantineTransitionOutcome.AgentCannotChange, (await store.AdmitAsync(Item(fresh), QuarantineActor.Agent, ct)).Outcome);
        Assert.Null(await store.GetAsync(fresh, ct));
        Assert.Equal(QuarantineTransitionOutcome.NotFound, (await store.AdmitAsync(Item(Guid.NewGuid()), QuarantineActor.Reader, ct)).Outcome);

        await store.AdmitAsync(Item(fresh), QuarantineActor.Reader, ct);
        await store.TransitionAsync(fresh, QuarantineState.Released, QuarantineActor.ForOwner(_owner), _none, ct);
        var again = await store.AdmitAsync(Item(fresh), QuarantineActor.Reader, ct);
        Assert.Equal(QuarantineTransitionOutcome.IllegalTransition, again.Outcome);
        Assert.Equal(QuarantineState.Released, (await store.GetAsync(fresh, ct))!.State);
    }

    [Fact]
    public async Task Release_persists_with_the_releaser_and_time()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        var now = _t0.AddDays(2);
        var store = new PostgresMailQuarantineStore(db.DataSource, new FixedTime(now));
        var id = await AdmitNewAsync(store, db.DataSource, await MailboxAsync(db.DataSource, ct), ct);

        var denied = await store.TransitionAsync(id, QuarantineState.Released, QuarantineActor.Reader, _none, ct);
        Assert.Equal(QuarantineTransitionOutcome.ReaderCannotRelease, denied.Outcome);
        Assert.Equal(QuarantineState.Held, (await store.GetAsync(id, ct))!.State);
        Assert.True(await Scalar<bool>(db.DataSource, "SELECT released_by IS NULL AND released_at IS NULL FROM mail_message WHERE id = $1", ct, id));

        var ok = await store.TransitionAsync(id, QuarantineState.Released, QuarantineActor.ForOwner(_owner), _none, ct);

        Assert.True(ok.IsAllowed);
        Assert.Equal(QuarantineState.Released, (await new PostgresMailQuarantineStore(db.DataSource).GetAsync(id, ct))!.State);
        Assert.Equal(_owner, await Scalar<Guid>(db.DataSource, "SELECT released_by FROM mail_message WHERE id = $1", ct, id));
        Assert.Equal(now, await Scalar<DateTimeOffset>(db.DataSource, "SELECT released_at FROM mail_message WHERE id = $1", ct, id));
        Assert.Equal(0, await store.CountHeldAsync(ct));
    }

    [Fact]
    public async Task Report_stores_the_approval_id_and_needs_one()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        var store = new PostgresMailQuarantineStore(db.DataSource);
        var id = await AdmitNewAsync(store, db.DataSource, await MailboxAsync(db.DataSource, ct), ct);
        var owner = QuarantineActor.ForOwner(_owner);

        var refused = await store.TransitionAsync(id, QuarantineState.Reported, owner, _none, ct);
        Assert.Equal(QuarantineTransitionOutcome.ReportNeedsApproval, refused.Outcome);
        Assert.Equal(QuarantineState.Held, (await store.GetAsync(id, ct))!.State);

        var approval = Guid.NewGuid();
        var ok = await store.TransitionAsync(id, QuarantineState.Reported, owner, new QuarantineTransitionContext(ReportApprovalId: approval), ct);

        Assert.True(ok.IsAllowed);
        Assert.Equal(QuarantineState.Reported, (await store.GetAsync(id, ct))!.State);
        Assert.Equal(approval, await Scalar<Guid>(db.DataSource, "SELECT report_approval_id FROM mail_message WHERE id = $1", ct, id));
    }

    [Fact]
    public async Task Purge_sets_purged_at_clears_the_body_and_respects_hold_and_due_time()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        var now = _t0.AddDays(91);
        var store = new PostgresMailQuarantineStore(db.DataSource, new FixedTime(now));
        var id = await AdmitNewAsync(store, db.DataSource, await MailboxAsync(db.DataSource, ct), ct);

        Assert.Equal(QuarantineTransitionOutcome.NotDueForPurge, (await store.TransitionAsync(id, QuarantineState.Purged, QuarantineActor.System, _none, ct)).Outcome);
        Assert.Equal(QuarantineTransitionOutcome.LegalHold,
            (await store.TransitionAsync(id, QuarantineState.Purged, QuarantineActor.System, new QuarantineTransitionContext(true, true), ct)).Outcome);
        Assert.Equal(QuarantineTransitionOutcome.NotOwner,
            (await store.TransitionAsync(id, QuarantineState.Purged, QuarantineActor.ForOwner(_owner), new QuarantineTransitionContext(true), ct)).Outcome);
        Assert.Equal(QuarantineState.Held, (await store.GetAsync(id, ct))!.State);
        Assert.Equal(3, await Scalar<int>(db.DataSource, "SELECT octet_length(body_ct) FROM mail_message WHERE id = $1", ct, id));

        var ok = await store.TransitionAsync(id, QuarantineState.Purged, QuarantineActor.System, new QuarantineTransitionContext(RetentionDue: true), ct);

        Assert.True(ok.IsAllowed);
        Assert.Equal(QuarantineState.Purged, (await store.GetAsync(id, ct))!.State);
        Assert.Equal(now, await Scalar<DateTimeOffset>(db.DataSource, "SELECT purged_at FROM mail_message WHERE id = $1", ct, id));
        Assert.True(await Scalar<bool>(db.DataSource,
            "SELECT body_ct IS NULL AND body_key_id IS NULL AND body_nonce IS NULL FROM mail_message WHERE id = $1", ct, id));
    }

    [Fact]
    public async Task Moved_on_items_are_terminal_and_missing_ids_are_not_found()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        var store = new PostgresMailQuarantineStore(db.DataSource);
        var id = await AdmitNewAsync(store, db.DataSource, await MailboxAsync(db.DataSource, ct), ct);
        var owner = QuarantineActor.ForOwner(_owner);
        await store.TransitionAsync(id, QuarantineState.Released, owner, _none, ct);

        foreach (var to in Enum.GetValues<QuarantineState>())
        {
            var result = await store.TransitionAsync(id, to, owner, new QuarantineTransitionContext(true, false, Guid.NewGuid()), ct);
            Assert.Equal(QuarantineTransitionOutcome.IllegalTransition, result.Outcome);
        }

        Assert.Equal(QuarantineTransitionOutcome.AgentCannotChange, (await store.TransitionAsync(id, QuarantineState.Released, QuarantineActor.Agent, _none, ct)).Outcome);
        Assert.Equal(QuarantineTransitionOutcome.NotFound, (await store.TransitionAsync(Guid.NewGuid(), QuarantineState.Released, owner, _none, ct)).Outcome);
    }

    [Fact]
    public async Task List_filters_by_state_oldest_first_and_skips_non_quarantine_rows()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        var store = new PostgresMailQuarantineStore(db.DataSource);
        var mailbox = await MailboxAsync(db.DataSource, ct);
        // An existing 0021-era row (no quarantine columns) and a delivered one are not quarantine items, and still insert and read as before.
        var legacy = await InsertReceivedAsync(db.DataSource, mailbox, ct);
        var delivered = await InsertReceivedAsync(db.DataSource, mailbox, ct, "delivered");
        var newer = await InsertReceivedAsync(db.DataSource, mailbox, ct);
        var older = await InsertReceivedAsync(db.DataSource, mailbox, ct);
        await store.AdmitAsync(Item(newer, received: _t0.AddHours(2)), QuarantineActor.Reader, ct);
        await store.AdmitAsync(Item(older, received: _t0.AddHours(1)), QuarantineActor.Reader, ct);
        await store.TransitionAsync(older, QuarantineState.Released, QuarantineActor.ForOwner(_owner), _none, ct);

        Assert.Equal([older, newer], (await store.ListAsync(null, ct)).Select(i => i.Id));
        Assert.Equal([newer], (await store.ListAsync(QuarantineState.Held, ct)).Select(i => i.Id));
        Assert.Equal([older], (await store.ListAsync(QuarantineState.Released, ct)).Select(i => i.Id));
        Assert.Empty(await store.ListAsync(QuarantineState.Purged, ct));
        Assert.Null(await store.GetAsync(legacy, ct));
        Assert.Null(await store.GetAsync(delivered, ct));
        Assert.Equal(1, await store.CountHeldAsync(ct));
    }

    [Fact]
    public async Task Digest_counts_states_and_ranks_held_domains()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        var store = new PostgresMailQuarantineStore(db.DataSource);
        var mailbox = await MailboxAsync(db.DataSource, ct);
        await AdmitNewAsync(store, db.DataSource, mailbox, ct, "a.example");
        await AdmitNewAsync(store, db.DataSource, mailbox, ct, "b.example");
        await AdmitNewAsync(store, db.DataSource, mailbox, ct, "b.example");
        await AdmitNewAsync(store, db.DataSource, mailbox, ct, "c.example");
        var released = await AdmitNewAsync(store, db.DataSource, mailbox, ct, "z.example");
        var reported = await AdmitNewAsync(store, db.DataSource, mailbox, ct, "z.example");
        await store.TransitionAsync(released, QuarantineState.Released, QuarantineActor.ForOwner(_owner), _none, ct);
        await store.TransitionAsync(reported, QuarantineState.Reported, QuarantineActor.ForOwner(_owner), new QuarantineTransitionContext(ReportApprovalId: Guid.NewGuid()), ct);

        var digest = await store.GetDigestAsync(new DateOnly(2026, 11, 18), 2, ct);

        Assert.Equal((4, 1, 0, 1), (digest.Held, digest.Released, digest.Purged, digest.Reported));
        Assert.Equal([new TopSenderDomain("b.example", 2), new TopSenderDomain("a.example", 1)], digest.TopSenderDomains);
        // The same figures as the pure builder over the listed items.
        var built = QuarantineDigestBuilder.Build(await store.ListAsync(null, ct), new DateOnly(2026, 11, 18), 2);
        Assert.Equal(built.TopSenderDomains, digest.TopSenderDomains);
        Assert.Equal((built.Held, built.Released, built.Purged, built.Reported), (digest.Held, digest.Released, digest.Purged, digest.Reported));
    }

    [Fact]
    public async Task The_schema_refuses_a_sender_domain_that_is_an_address_or_upper_case()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDbAsync(ct);
        var id = await InsertReceivedAsync(db.DataSource, await MailboxAsync(db.DataSource, ct), ct);

        await Assert.ThrowsAsync<PostgresException>(() =>
            Exec(db.DataSource, "UPDATE mail_message SET sender_domain = 'alice@spam.example' WHERE id = $1", [id], ct));
        await Assert.ThrowsAsync<PostgresException>(() =>
            Exec(db.DataSource, "UPDATE mail_message SET sender_domain = 'Spam.Example' WHERE id = $1", [id], ct));
    }
}
