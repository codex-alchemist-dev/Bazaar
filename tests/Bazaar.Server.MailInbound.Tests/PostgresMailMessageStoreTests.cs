using System.Reflection;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Mail;
using Bazaar.Server.Mail.Quarantine;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.MailInbound.Tests;

// Database tests: they skip on Windows without BAZAAR_TEST_PG and otherwise need a PostgreSQL (BAZAAR_TEST_PG or Docker).
public sealed class PostgresMailMessageStoreTests
{
    private const string _skipReason = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

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

    private static async Task<Guid> MailboxAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        var domain = Guid.NewGuid();
        var mailbox = Guid.NewGuid();
        await Exec(ds, "INSERT INTO mail_domain (id, domain, backend_kind) VALUES ($1, $2, 'imap_smtp')", [domain, $"{domain:N}.example"], ct);
        await Exec(ds, "INSERT INTO mail_mailbox (id, domain_id, local_part) VALUES ($1, $2, 'agent')", [mailbox, domain], ct);
        return mailbox;
    }

    private static async Task Exec(NpgsqlDataSource ds, string sql, object[] args, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> Scalar<T>(NpgsqlDataSource ds, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var a in args)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        }

        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static NewInboundMessage Message(Guid mailbox, string backendId, string? rfcId, string domain = "sender.example") =>
        new(Guid.NewGuid(), mailbox, rfcId, backendId, domain, "spf=pass dkim=none dmarc=none", [], _t0, new SealedBody(Guid.NewGuid(), [9, 8, 7], [1, 2, 3]));

    [Fact]
    public void Only_the_sealed_body_read_selects_body_columns()
    {
        var sql = typeof(PostgresMailMessageStore)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        foreach (var (name, text) in sql.Where(kv => kv.Value.StartsWith("SELECT", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("from_addr", text, StringComparison.Ordinal);
            var selected = text[..text.IndexOf(" FROM ", StringComparison.Ordinal)];
            Assert.Equal(name is "_readBody" or "_pending", selected.Contains("body_ct", StringComparison.Ordinal));
        }

        Assert.DoesNotContain("from_addr", sql["_insert"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Insert_stores_a_received_row_with_the_domain_and_a_sealed_body()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailin", MigrateAsync, ct);
        var store = new PostgresMailMessageStore(db.DataSource);
        var mailbox = await MailboxAsync(db.DataSource, ct);
        var message = Message(mailbox, "b1", "a@sender.example");

        var result = await store.InsertReceivedAsync(message, ct);

        Assert.Equal(InboundInsertOutcome.Inserted, result.Outcome);
        Assert.Equal(message.Id, result.Id);
        Assert.Equal("received", await Scalar<string>(db.DataSource, "SELECT state FROM mail_message WHERE id = $1", ct, message.Id));
        Assert.Equal("sender.example", await Scalar<string>(db.DataSource, "SELECT sender_domain FROM mail_message WHERE id = $1", ct, message.Id));
        Assert.Equal(0L, await Scalar<long>(db.DataSource, "SELECT count(*) FROM mail_message WHERE id = $1 AND from_addr IS NOT NULL", ct, message.Id));
        Assert.Equal("spf=pass dkim=none dmarc=none", await Scalar<string>(db.DataSource, "SELECT auth_json ->> 'summary' FROM mail_message WHERE id = $1", ct, message.Id));
    }

    [Fact]
    public async Task Duplicate_delivery_is_found_by_message_id_or_back_end_id()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailin", MigrateAsync, ct);
        var store = new PostgresMailMessageStore(db.DataSource);
        var mailbox = await MailboxAsync(db.DataSource, ct);
        var first = Message(mailbox, "b1", "a@sender.example");
        await store.InsertReceivedAsync(first, ct);

        var sameRfc = await store.InsertReceivedAsync(Message(mailbox, "b2", "a@sender.example"), ct);
        var sameBackend = await store.InsertReceivedAsync(Message(mailbox, "b1", "other@sender.example"), ct);
        var noRfcA = await store.InsertReceivedAsync(Message(mailbox, "b3", null), ct);
        var noRfcB = await store.InsertReceivedAsync(Message(mailbox, "b3", null), ct);

        Assert.Equal((InboundInsertOutcome.Duplicate, first.Id), (sameRfc.Outcome, sameRfc.Id));
        Assert.Equal((InboundInsertOutcome.Duplicate, first.Id), (sameBackend.Outcome, sameBackend.Id));
        Assert.Equal(InboundInsertOutcome.Inserted, noRfcA.Outcome);
        Assert.Equal((InboundInsertOutcome.Duplicate, noRfcA.Id), (noRfcB.Outcome, noRfcB.Id));
        Assert.Equal(2L, await Scalar<long>(db.DataSource, "SELECT count(*) FROM mail_message", ct));

        // The same Message-ID in another mailbox is another message.
        var other = await store.InsertReceivedAsync(Message(await MailboxAsync(db.DataSource, ct), "b1", "a@sender.example"), ct);
        Assert.Equal(InboundInsertOutcome.Inserted, other.Outcome);
    }

    [Fact]
    public async Task Concurrent_inserts_of_one_message_id_store_one_row()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailin", MigrateAsync, ct);
        var store = new PostgresMailMessageStore(db.DataSource);
        var mailbox = await MailboxAsync(db.DataSource, ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.InsertReceivedAsync(Message(mailbox, "b" + i, "race@sender.example"), ct)));

        Assert.Single(results, r => r.Outcome == InboundInsertOutcome.Inserted);
        Assert.Single(results.Select(r => r.Id).Distinct());
        Assert.Equal(1L, await Scalar<long>(db.DataSource, "SELECT count(*) FROM mail_message", ct));
    }

    [Fact]
    public async Task The_gc_message_and_delivery_are_recorded_once()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailin", MigrateAsync, ct);
        var store = new PostgresMailMessageStore(db.DataSource);
        var mailbox = await MailboxAsync(db.DataSource, ct);
        var message = Message(mailbox, "b1", "a@sender.example");
        await store.InsertReceivedAsync(message, ct);
        var gc = Guid.NewGuid();

        await store.RecordGcMessageAsync(message.Id, gc, ct);
        var resumed = await store.InsertReceivedAsync(Message(mailbox, "b1", "a@sender.example"), ct);
        var delivered = await store.MarkDeliveredAsync(message.Id, ct);
        var again = await store.MarkDeliveredAsync(message.Id, ct);
        var after = await store.InsertReceivedAsync(Message(mailbox, "b1", "a@sender.example"), ct);

        Assert.Equal((InboundStoreState.Received, gc), (resumed.State, resumed.GcMessageId));
        Assert.True(delivered);
        Assert.False(again);
        Assert.Equal(InboundStoreState.Delivered, after.State);
    }

    [Fact]
    public async Task An_actor_cannot_read_the_sealed_body_and_a_purged_body_is_gone()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailin", MigrateAsync, ct);
        var store = new PostgresMailMessageStore(db.DataSource);
        var mailbox = await MailboxAsync(db.DataSource, ct);
        var message = Message(mailbox, "b1", "a@sender.example");
        await store.InsertReceivedAsync(message, ct);

        Assert.Equal(MailBodyReadOutcome.Denied, (await store.ReadSealedBodyAsync(message.Id, MailAccessor.Agent, ct)).Outcome);
        Assert.Equal(MailBodyReadOutcome.Denied, (await store.ReadSealedBodyAsync(message.Id, MailAccessor.System, ct)).Outcome);
        var read = await store.ReadSealedBodyAsync(message.Id, MailAccessor.Reader, ct);
        Assert.Equal(MailBodyReadOutcome.Ok, read.Outcome);
        Assert.Equal(message.Body.Ciphertext, read.Body!.Ciphertext);
        Assert.Equal(MailBodyReadOutcome.NotFound, (await store.ReadSealedBodyAsync(Guid.NewGuid(), MailAccessor.Owner, ct)).Outcome);

        await Exec(db.DataSource, "UPDATE mail_message SET body_ct = NULL, body_key_id = NULL, body_nonce = NULL WHERE id = $1", [message.Id], ct);
        Assert.Equal(MailBodyReadOutcome.NotFound, (await store.ReadSealedBodyAsync(message.Id, MailAccessor.Reader, ct)).Outcome);
    }

    [Fact]
    public async Task The_pipeline_runs_end_to_end_on_postgres_for_clean_held_escalated_and_duplicate_mail()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("mailin", MigrateAsync, ct);
        var mailbox = await MailboxAsync(db.DataSource, ct);
        var h = new Harness();
        var binding = h.Mailbox with { MailboxId = mailbox };
        var quarantine = new PostgresMailQuarantineStore(db.DataSource);
        var options = new MailScreeningOptions();
        var pipeline = new InboundMailPipeline(
            new PostgresMailMessageStore(db.DataSource), quarantine, new FakeSealer(),
            new MailScreeningService(new InMemoryProtectedMailDomains(), options), options,
            h.Gc, h.Reader, h.Reader, h.Reader);
        h.Reader.Decide = i => i.Subject == "escalate me"
            ? new MailTriageHandover(i.MailMessageId, MailTriageVerdict.Escalate, [MailTriageReasonCodes.RequestsCredentials])
            : new MailTriageHandover(i.MailMessageId, MailTriageVerdict.NeedsAttention, [MailTriageReasonCodes.SignUpConfirmation]);
        h.Backend.Queue.Add(Mime.Raw("b1", Mime.Plain("Svc <noreply@svc.example.test>", "welcome", "hi", "1@svc", "spf=pass"), _t0));
        h.Backend.Queue.Add(Mime.Raw("b2", Mime.Plain("Bad <x@bad.example.test>", "spam", "hi", "2@bad", "spf=fail"), _t0.AddMinutes(1)));
        h.Backend.Queue.Add(Mime.Raw("b3", Mime.Plain("Odd <y@odd.example.test>", "escalate me", "hi", "3@odd", "spf=pass"), _t0.AddMinutes(2)));
        binding = binding with { Backend = h.Backend };

        var first = await pipeline.ProcessMailboxAsync(binding, ct);
        var second = await pipeline.ProcessMailboxAsync(binding, ct);

        Assert.Equal((1, 2, 0), (first.Delivered, first.Quarantined, first.Failures.Count));
        Assert.Equal(3, second.Duplicates);
        Assert.Equal(1L, await Scalar<long>(db.DataSource, "SELECT count(*) FROM mail_message WHERE state = 'delivered'", ct));
        Assert.Equal(2L, await Scalar<long>(db.DataSource, "SELECT count(*) FROM mail_message WHERE state = 'held'", ct));
        Assert.Equal(3L, await Scalar<long>(db.DataSource, "SELECT count(*) FROM mail_message", ct));
        var held = await quarantine.ListAsync(QuarantineState.Held, ct);
        Assert.Equal(["bad.example.test", "odd.example.test"], held.Select(i => i.SenderDomain));
        Assert.Equal(["reader:requests_credentials"], held[1].ReasonCodes);
        // The escalated message was announced (metadata only) before its reader read it; the held one never was.
        Assert.Equal(2, h.Gc.External.Count);
        Assert.Equal(2, h.Gc.Notices.Count);
        Assert.Equal(2, h.Reader.Started.Count);
    }
}
