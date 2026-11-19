using System.Text;
using Bazaar.RateLimits;
using Bazaar.Server.Mail;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.MailOutbound.Tests;

// Database tests: they skip on Windows without BAZAAR_TEST_PG and otherwise need a PostgreSQL (BAZAAR_TEST_PG or Docker).
public sealed class PostgresOutboundMailStoreTests
{
    private const string _skipReason = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";
    private const string _secretBody = "the-secret-body-text";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Applies the mail namespace (0021 to 0023) from the module assembly's embedded SQL, in version order, the same files the host reads.</summary>
    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(MailComponent).Assembly;
        const string marker = ".Sql.components.mail.";
        var files = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(marker, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var name in files)
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<PgTestDatabase> CreateDbAsync()
    {
        var db = await PgTestDatabase.CreateAsync("mailout", MigrateAsync, Ct);
        var domain = Guid.NewGuid();
        await Exec(db.DataSource, "INSERT INTO mail_domain (id, domain, backend_kind) VALUES ($1, 'mail.test', 'imap_smtp')", [domain]);
        await Exec(db.DataSource, "INSERT INTO mail_mailbox (id, domain_id, local_part) VALUES ($1, $2, 'charles')", [Guid.NewGuid(), domain]);
        return db;
    }

    private static async Task Exec(NpgsqlDataSource ds, string sql, object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var arg in args) cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource ds, string sql, params object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var arg in args) cmd.Parameters.Add(new NpgsqlParameter { Value = arg });
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static OutboundRequest Request(string nonce = "n1", MailTaint taint = MailTaint.Clean) =>
        new("agent-1", new MailboxRef("mail.test", "charles"), ["friend@example.org"], "a private subject", _secretBody, 0, 0, taint, true, false, false, nonce);

    private static StoredOutbound Fresh(OutboundRequest request, OutboundStatus status = OutboundStatus.Sending) =>
        new(OutboundIdempotency.Derive(request), Guid.NewGuid(), null, status, 0, null, "Footer.", 1, OutboundReason.Tier1Template, _t0, request);

    [Fact]
    public async Task Insert_stores_the_message_in_state_sending_with_an_encrypted_body_and_reads_it_back()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        await using var db = await CreateDbAsync();
        var store = new PostgresOutboundMailStore(db.DataSource, new TestBodyProtector());
        var record = Fresh(Request());

        var (_, created) = await store.TryInsertAsync(record, Ct);
        var found = await store.FindAsync(record.IdempotencyKey, Ct);

        Assert.True(created);
        Assert.Equal(OutboundStatus.Sending, found!.Status);
        Assert.Equal(record.MessageId, found.MessageId);
        Assert.Equal("Footer.", found.Footer);
        Assert.Equal(OutboundReason.Tier1Template, found.Reason);
        Assert.Equal(1, found.Tier);
        Assert.Equal(_secretBody, found.Request!.TextBody);
        Assert.Equal(["friend@example.org"], found.Request.Recipients);
        Assert.Equal(new MailboxRef("mail.test", "charles"), found.Request.From);
        Assert.Equal("sending", await Scalar<string>(db.DataSource, "SELECT state FROM mail_message WHERE id = $1", record.MessageId));

        var ciphertext = await Scalar<byte[]>(db.DataSource, "SELECT body_ct FROM mail_message WHERE id = $1", record.MessageId);
        Assert.DoesNotContain(_secretBody, Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
        Assert.DoesNotContain("friend@example.org", Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_key_inserts_nothing_and_returns_the_stored_row()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        await using var db = await CreateDbAsync();
        var store = new PostgresOutboundMailStore(db.DataSource, new TestBodyProtector());
        var first = Fresh(Request());
        await store.TryInsertAsync(first, Ct);

        var (existing, created) = await store.TryInsertAsync(first with { MessageId = Guid.NewGuid() }, Ct);

        Assert.False(created);
        Assert.Equal(first.MessageId, existing.MessageId);
        Assert.Equal(1L, await Scalar<long>(db.DataSource, "SELECT count(*) FROM mail_message"));
        Assert.Equal(1L, await Scalar<long>(db.DataSource, "SELECT count(*) FROM mail_outbound"));
    }

    [Fact]
    public async Task An_unprovisioned_mailbox_is_refused_and_leaves_no_rows()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        await using var db = await CreateDbAsync();
        var store = new PostgresOutboundMailStore(db.DataSource, new TestBodyProtector());
        var other = Request() with { From = new MailboxRef("mail.test", "nobody") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryInsertAsync(Fresh(other), Ct));

        Assert.Equal(0L, await Scalar<long>(db.DataSource, "SELECT count(*) FROM mail_message"));
    }

    [Fact]
    public async Task Update_is_a_compare_and_set_and_terminal_failure_clears_the_stored_text()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        await using var db = await CreateDbAsync();
        var store = new PostgresOutboundMailStore(db.DataSource, new TestBodyProtector());
        var record = Fresh(Request());
        await store.TryInsertAsync(record, Ct);

        var claimed = record with { Attempts = 1, UpdatedAt = _t0.AddSeconds(1) };
        Assert.True(await store.TryUpdateAsync(record, claimed, Ct));
        Assert.False(await store.TryUpdateAsync(record, claimed with { Attempts = 5 }, Ct)); // a second claimant loses

        var pending = claimed with { Status = OutboundStatus.Pending, NextAttemptAt = _t0.AddSeconds(30), UpdatedAt = _t0.AddSeconds(2) };
        Assert.True(await store.TryUpdateAsync(claimed, pending, Ct));
        Assert.Equal("queued", await Scalar<string>(db.DataSource, "SELECT state FROM mail_message WHERE id = $1", record.MessageId));
        Assert.NotNull((await store.FindAsync(record.IdempotencyKey, Ct))!.Request);

        var failed = pending with { Status = OutboundStatus.Failed, NextAttemptAt = null, UpdatedAt = _t0.AddSeconds(40) };
        Assert.True(await store.TryUpdateAsync(pending, failed, Ct));
        var found = await store.FindAsync(record.IdempotencyKey, Ct);
        Assert.Equal(OutboundStatus.Failed, found!.Status);
        Assert.Null(found.Request);
        Assert.Equal("failed", await Scalar<string>(db.DataSource, "SELECT state FROM mail_message WHERE id = $1", record.MessageId));
        Assert.True(await Scalar<bool>(db.DataSource, "SELECT body_ct IS NULL AND body_key_id IS NULL AND body_nonce IS NULL FROM mail_message WHERE id = $1", record.MessageId));
    }

    [Fact]
    public async Task ListDue_finds_approvals_due_retries_and_stale_sending_rows_only()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        await using var db = await CreateDbAsync();
        var store = new PostgresOutboundMailStore(db.DataSource, new TestBodyProtector());
        var awaiting = Fresh(Request("a"), OutboundStatus.AwaitingApproval);
        var due = Fresh(Request("b"), OutboundStatus.Pending) with { NextAttemptAt = _t0 };
        var later = Fresh(Request("c"), OutboundStatus.Pending) with { NextAttemptAt = _t0.AddHours(1) };
        var stale = Fresh(Request("d")) with { UpdatedAt = _t0.AddMinutes(-10) };
        var fresh = Fresh(Request("e")) with { UpdatedAt = _t0 };
        var sent = Fresh(Request("f"), OutboundStatus.Sent);
        foreach (var record in new[] { awaiting, due, later, stale, fresh, sent }) await store.TryInsertAsync(record, Ct);

        var keys = await store.ListDueAsync(_t0, _t0.AddMinutes(-5), 10, Ct);

        Assert.Equal(
            new[] { awaiting, due, stale }.Select(r => r.IdempotencyKey).Order(StringComparer.Ordinal),
            keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_pipeline_on_postgres_sends_once_across_a_restart_and_holds_a_hard_tainted_send_for_approval()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        await using var db = await CreateDbAsync();
        var protector = new TestBodyProtector();
        var clock = new TestClock(_t0);
        var backend = new FakeBackend();
        var approvals = new FakeApprovals();
        var audit = new FakeAuditLog();
        var policy = new CountingPolicy(new OutboundMailPolicy(
            new FakeDirectory(), new FakeApproved("friend@example.org"), new InMemoryRateLimiter(clock), new FakeSettings()));

        OutboundMailPipeline Build(FakeBackend b) => new(
            policy, new PostgresOutboundMailStore(db.DataSource, protector), approvals, new FakeResolver(b), audit,
            new OutboundPipelineOptions(MaxAttempts: 3, RetryBaseDelay: TimeSpan.FromSeconds(10)), clock);

        var sent = await Build(backend).SendAsync(Request(), Ct);
        var afterRestart = await Build(new FakeBackend()).SendAsync(Request(), Ct);

        Assert.Equal(OutboundSendStatus.Sent, sent.Status);
        Assert.Equal(OutboundSendStatus.AlreadySent, afterRestart.Status);
        Assert.Equal(1, backend.Delivered);
        Assert.Equal(1, policy.Evaluations);
        Assert.Equal("sent", await Scalar<string>(db.DataSource, "SELECT state FROM mail_message WHERE id = $1", sent.MessageId!.Value));
        Assert.Equal("backend-1", await Scalar<string>(db.DataSource, "SELECT backend_id FROM mail_message WHERE id = $1", sent.MessageId!.Value));
        Assert.Equal("sent", await Scalar<string>(db.DataSource, "SELECT status FROM mail_outbound WHERE idempotency_key = $1", sent.IdempotencyKey));
        Assert.Equal(1, await Scalar<int>(db.DataSource, "SELECT attempts FROM mail_outbound WHERE idempotency_key = $1", sent.IdempotencyKey));

        var held = await Build(backend).SendAsync(Request("h", MailTaint.Hard), Ct);
        Assert.Equal(OutboundSendStatus.AwaitingApproval, held.Status);
        Assert.Equal(held.ApprovalId, await Scalar<Guid>(db.DataSource, "SELECT approval_id FROM mail_outbound WHERE idempotency_key = $1", held.IdempotencyKey));
        approvals.Set(held.ApprovalId!.Value, MailApprovalStatus.Approved);

        var released = await Build(backend).ResumeAsync(held.IdempotencyKey, Ct);

        Assert.Equal(OutboundSendStatus.Sent, released!.Status);
        Assert.Equal(2, backend.Delivered);
        Assert.Equal(2, policy.Evaluations);
    }

    [Fact]
    public async Task The_migration_widens_the_state_lists_without_touching_existing_rows()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        await using var db = await CreateDbAsync();

        // 0021 states still work; the new ones are allowed; an unknown one is still refused.
        var mailbox = await Scalar<Guid>(db.DataSource, "SELECT id FROM mail_mailbox LIMIT 1");
        await Exec(db.DataSource, "INSERT INTO mail_message (id, mailbox_id, direction, state) VALUES ($1, $2, 'inbound', 'received')", [Guid.NewGuid(), mailbox]);
        await Exec(db.DataSource, "INSERT INTO mail_message (id, mailbox_id, direction, state) VALUES ($1, $2, 'outbound', 'sending')", [Guid.NewGuid(), mailbox]);
        await Assert.ThrowsAsync<PostgresException>(() =>
            Exec(db.DataSource, "INSERT INTO mail_message (id, mailbox_id, direction, state) VALUES ($1, $2, 'outbound', 'bogus')", [Guid.NewGuid(), mailbox]));
    }
}
