using Bazaar.GitHub.Events;
using Bazaar.GitHub.Ingress;
using Bazaar.GitHub.Processing;
using Bazaar.GitHub.Webhooks;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Bazaar.GitHub.Tests.Processing;

/// <summary>The webhook processor (doc 21, Ingress) against real Postgres rows: claim concurrency, retry with backoff, poison rows, ordering.</summary>
public sealed class GitHubWebhookProcessorTests
{
    private const string _agent = "bob-bazaar[bot]";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private sealed class RecordingSink : IGitHubEventSink
    {
        public List<GitHubEventDelivery> Deliveries { get; } = [];

        public int FailuresRemaining { get; set; }

        public Task DeliverAsync(GitHubEventDelivery delivery, CancellationToken cancellationToken)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("sink down");
            }

            lock (Deliveries)
            {
                Deliveries.Add(delivery);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(GitHubComponent).Assembly;
        foreach (var suffix in new[] { "0001_github_tables.sql", "0002_github_delivery_payload.sql", "0003_github_delivery_processed.sql" })
        {
            var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static string ReviewRequested(string pr, string updatedAt) => $$$"""
        {"action": "review_requested", "requested_reviewer": {"login": "{{{_agent}}}"}, "sender": {"login": "boss"}, "repository": {"node_id": "R_1"},
         "pull_request": {"node_id": "{{{pr}}}", "updated_at": "{{{updatedAt}}}", "head": {"sha": "abc"}, "user": {"login": "boss"} } }
        """;

    private static async Task Insert(PgTestDatabase db, string id, string eventType, string? payload, DateTimeOffset received, CancellationToken ct) =>
        Assert.True(await new Bazaar.GitHub.Webhooks.PgGitHubDeliveryStore(db.DataSource).TryRecordAsync(GitHubWebhookEndpoint.Source, id, eventType, received, payload, ct));

    private static GitHubWebhookProcessor Build(PgTestDatabase db, RecordingSink sink, Clock clock) =>
        new(new PgGitHubDeliveryInbox(db.DataSource), sink, new GitHubIngressOptions { AgentLogins = [_agent] },
            new GitHubWebhookProcessorOptions(), NullLogger<GitHubWebhookProcessor>.Instance, clock);

    private static async Task<(DateTimeOffset? Processed, int Attempts, string? Error)> RowAsync(PgTestDatabase db, string id, CancellationToken ct)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT processed_at, process_attempts, process_error FROM github_delivery WHERE delivery_id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct));
        return (r.IsDBNull(0) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc)), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    [Fact]
    public async Task A_stored_webhook_is_mapped_delivered_once_and_marked_processed()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghproc1", MigrateAsync, ct);
        await Insert(db, "d1", "pull_request", ReviewRequested("PR_1", "2026-11-22T08:00:00Z"), _t0, ct);
        var sink = new RecordingSink();
        var processor = Build(db, sink, new Clock(_t0));

        Assert.Equal(1, await processor.ProcessBatchAsync(ct));
        Assert.Equal(0, await processor.ProcessBatchAsync(ct));

        var delivery = Assert.Single(sink.Deliveries);
        Assert.Equal("github:d1", delivery.DedupeKey);
        Assert.True(delivery.Apply);
        Assert.NotEmpty(delivery.Effects);
        Assert.NotNull((await RowAsync(db, "d1", ct)).Processed);
    }

    [Fact]
    public async Task A_sink_failure_leaves_the_row_unprocessed_and_retries_after_the_backoff()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghproc2", MigrateAsync, ct);
        await Insert(db, "d1", "pull_request", ReviewRequested("PR_1", "2026-11-22T08:00:00Z"), _t0, ct);
        var sink = new RecordingSink { FailuresRemaining = 2 };
        var clock = new Clock(_t0);
        var processor = Build(db, sink, clock);

        Assert.Equal(1, await processor.ProcessBatchAsync(ct));
        Assert.Null((await RowAsync(db, "d1", ct)).Processed);

        // Inside the first backoff (30 s) the row is not due.
        clock.Now = _t0.AddSeconds(10);
        Assert.Equal(0, await processor.ProcessBatchAsync(ct));

        // Second failure doubles the wait to 60 s.
        clock.Now = _t0.AddSeconds(31);
        Assert.Equal(1, await processor.ProcessBatchAsync(ct));
        clock.Now = _t0.AddSeconds(31 + 59);
        Assert.Equal(0, await processor.ProcessBatchAsync(ct));

        clock.Now = _t0.AddSeconds(31 + 61);
        Assert.Equal(1, await processor.ProcessBatchAsync(ct));
        Assert.Single(sink.Deliveries);
        var row = await RowAsync(db, "d1", ct);
        Assert.NotNull(row.Processed);
        Assert.Equal(3, row.Attempts);
        Assert.Null(row.Error);
    }

    [Fact]
    public async Task A_poison_row_is_marked_processed_with_a_reason_and_never_loops()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghproc3", MigrateAsync, ct);
        await Insert(db, "nopayload", "pull_request", null, _t0, ct);
        await Insert(db, "badshape", "pull_request", """{"action": "opened"}""", _t0.AddSeconds(1), ct);
        await Insert(db, "ping", "ping", """{"zen": "x"}""", _t0.AddSeconds(2), ct);
        var sink = new RecordingSink();
        var processor = Build(db, sink, new Clock(_t0.AddMinutes(1)));

        Assert.Equal(3, await processor.ProcessBatchAsync(ct));
        Assert.Equal(0, await processor.ProcessBatchAsync(ct));

        Assert.Empty(sink.Deliveries);
        var noPayload = await RowAsync(db, "nopayload", ct);
        Assert.NotNull(noPayload.Processed);
        Assert.Contains("payload", noPayload.Error);
        Assert.NotNull((await RowAsync(db, "badshape", ct)).Error);
        var ping = await RowAsync(db, "ping", ct);
        Assert.NotNull(ping.Processed);
        Assert.Null(ping.Error);
    }

    [Fact]
    public async Task Concurrent_claims_never_hand_the_same_row_to_two_processors()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghproc4", MigrateAsync, ct);
        const int rows = 60;
        for (var i = 0; i < rows; i++)
        {
            await Insert(db, $"d{i:D3}", "pull_request", ReviewRequested($"PR_{i}", "2026-11-22T08:00:00Z"), _t0.AddSeconds(i), ct);
        }

        var queue = new PgGitHubDeliveryInbox(db.DataSource);
        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(async () =>
            {
                var mine = new List<string>();
                while (true)
                {
                    var batch = await queue.ClaimAsync(7, _t0.AddMinutes(1), TimeSpan.FromMinutes(5), ct);
                    if (batch.Count == 0)
                    {
                        return mine;
                    }

                    mine.AddRange(batch.Select(b => b.DeliveryId));
                }
            }, ct)));

        var all = claims.SelectMany(c => c).ToList();
        Assert.Equal(rows, all.Count);
        Assert.Equal(rows, all.Distinct().Count());
    }

    [Fact]
    public async Task A_claimed_row_returns_after_the_lease_if_nobody_completes_it()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghproc5", MigrateAsync, ct);
        await Insert(db, "d1", "pull_request", ReviewRequested("PR_1", "2026-11-22T08:00:00Z"), _t0, ct);
        var queue = new PgGitHubDeliveryInbox(db.DataSource);

        Assert.Single(await queue.ClaimAsync(10, _t0, TimeSpan.FromMinutes(5), ct));
        Assert.Empty(await queue.ClaimAsync(10, _t0.AddMinutes(1), TimeSpan.FromMinutes(5), ct));
        var again = Assert.Single(await queue.ClaimAsync(10, _t0.AddMinutes(6), TimeSpan.FromMinutes(5), ct));
        Assert.Equal(2, again.Attempts);
    }

    [Fact]
    public async Task An_older_event_for_the_same_pr_is_delivered_without_applying()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghproc6", MigrateAsync, ct);
        await Insert(db, "new", "pull_request", ReviewRequested("PR_1", "2026-11-22T08:30:00Z"), _t0, ct);
        await Insert(db, "old", "pull_request", ReviewRequested("PR_1", "2026-11-22T08:00:00Z"), _t0.AddSeconds(1), ct);
        var sink = new RecordingSink();

        await Build(db, sink, new Clock(_t0.AddMinutes(1))).ProcessBatchAsync(ct);

        Assert.Equal(2, sink.Deliveries.Count);
        Assert.True(sink.Deliveries[0].Apply);
        Assert.False(sink.Deliveries[1].Apply);
        Assert.Empty(sink.Deliveries[1].Effects);
    }

    [Fact]
    public async Task Polled_rows_and_other_sources_are_left_alone()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghproc7", MigrateAsync, ct);
        await new Bazaar.GitHub.Ingress.PgGitHubDeliveryStore(db.DataSource).MarkProcessedAsync("poll", "e1", "pull_request", _t0, ct);
        await using (var cmd = db.DataSource.CreateCommand("INSERT INTO github_delivery (source, delivery_id, event, received_at) VALUES ('other', 'x', 'push', now())"))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        Assert.Empty(await new PgGitHubDeliveryInbox(db.DataSource).ClaimAsync(10, _t0.AddDays(1), TimeSpan.FromMinutes(5), ct));
    }
}
