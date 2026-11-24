using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>The GC message version and mention readers against the real <c>gc_message_version</c> and <c>gc_mention</c> tables.</summary>
public sealed class PostgresGcSourcesTests
{
    private static readonly Guid _author = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    [Fact]
    public async Task Versions_come_in_seq_order_after_the_cursor_with_ciphertext_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ExportReaderDb.OpenAsync(ct);
        var gc = await db.CreateGcAsync();
        var m1 = Guid.CreateVersion7();
        var m2 = Guid.CreateVersion7();
        await db.AddMessageAsync(gc, m1, _author, [1, 4, 6], lastIsTombstone: true);
        await db.AddMessageAsync(gc, m2, _author, [2, 3]);
        var source = new PostgresGcMessageVersionSource(db.DataSource, new FixedGrantAccess(GcReadGrant.Full));

        var all = await ToListAsync(source.ReadAsync(gc, 0, 100, ExportReaderPeople.Viewer, ct));

        Assert.Equal([1L, 2, 3, 4, 6], all.Select(v => v.Seq));
        Assert.Equal([m1, m2, m2, m1, m1], all.Select(v => v.MessageId));
        Assert.Equal([1, 1, 2, 2, 3], all.Select(v => v.Version));
        Assert.Equal(new byte[] { 1, 2, 1 }, all[0].Ciphertext);
        Assert.Equal(new byte[] { 9, 9 }, all[0].Nonce);
        Assert.NotNull(all[0].KeyId);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 8, 0, 1, TimeSpan.Zero), all[0].CreatedAt);
        var tombstone = all[^1];
        Assert.True(tombstone.Tombstone);
        Assert.Null(tombstone.Ciphertext);
        Assert.Null(tombstone.Nonce);
        Assert.Null(tombstone.KeyId);

        var page = await ToListAsync(source.ReadAsync(gc, 2, 2, ExportReaderPeople.Viewer, ct));
        Assert.Equal([3L, 4], page.Select(v => v.Seq));
    }

    [Fact]
    public async Task Versions_stop_at_the_grants_last_seq()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ExportReaderDb.OpenAsync(ct);
        var gc = await db.CreateGcAsync();
        await db.AddMessageAsync(gc, Guid.CreateVersion7(), _author, [1, 2, 3, 4]);
        var source = new PostgresGcMessageVersionSource(db.DataSource, new FixedGrantAccess(GcReadGrant.Through(2)));

        var rows = await ToListAsync(source.ReadAsync(gc, 0, 100, ExportReaderPeople.Viewer, ct));

        Assert.Equal([1L, 2], rows.Select(v => v.Seq));
    }

    [Fact]
    public async Task Versions_of_another_gc_are_not_returned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ExportReaderDb.OpenAsync(ct);
        var gc = await db.CreateGcAsync();
        var other = await db.CreateGcAsync();
        await db.AddMessageAsync(gc, Guid.CreateVersion7(), _author, [1]);
        await db.AddMessageAsync(other, Guid.CreateVersion7(), _author, [1, 2]);
        var source = new PostgresGcMessageVersionSource(db.DataSource, new FixedGrantAccess(GcReadGrant.Full));

        var rows = await ToListAsync(source.ReadAsync(gc, 0, 100, ExportReaderPeople.Viewer, ct));

        Assert.Single(rows);
    }

    [Fact]
    public async Task Versions_are_refused_when_the_viewer_has_no_grant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ExportReaderDb.OpenAsync(ct);
        var gc = await db.CreateGcAsync();
        var source = new PostgresGcMessageVersionSource(db.DataSource, new FixedGrantAccess(GcReadGrant.Denied));

        await Assert.ThrowsAsync<GcAccessDeniedException>(async () => await ToListAsync(source.ReadAsync(gc, 0, 10, ExportReaderPeople.Viewer, ct)));
    }

    [Fact]
    public async Task Mentions_page_by_seq_and_target_without_skipping_or_repeating()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ExportReaderDb.OpenAsync(ct);
        var gc = await db.CreateGcAsync();
        var t1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var t2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var t3 = Guid.Parse("ff000000-0000-0000-0000-000000000003");
        foreach (var (seq, target) in new (long, Guid)[] { (5, t3), (5, t1), (5, t2), (7, t2), (9, t1) })
        {
            await db.ExecAsync("INSERT INTO gc_mention (gc_id, seq, target_principal_id) VALUES ($1, $2, $3)", gc, seq, target);
        }

        var source = new PostgresGcMentionSource(db.DataSource, new FixedGrantAccess(GcReadGrant.Full));
        var seen = new List<GcMentionRecord>();
        long afterSeq = 0;
        var afterTarget = Guid.Empty;
        while (true)
        {
            var page = await ToListAsync(source.ReadAsync(gc, afterSeq, afterTarget, 2, ExportReaderPeople.Viewer, ct));
            seen.AddRange(page);
            if (page.Count < 2)
            {
                break;
            }

            afterSeq = page[^1].Seq;
            afterTarget = page[^1].TargetPrincipalId;
        }

        Assert.Equal([(5L, t1), (5L, t2), (5L, t3), (7L, t2), (9L, t1)], seen.Select(m => (m.Seq, m.TargetPrincipalId)));
    }

    [Fact]
    public async Task Mentions_stop_at_the_grants_last_seq_and_refuse_a_denied_viewer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await ExportReaderDb.OpenAsync(ct);
        var gc = await db.CreateGcAsync();
        var target = Guid.CreateVersion7();
        foreach (var seq in new long[] { 1, 2, 3 })
        {
            await db.ExecAsync("INSERT INTO gc_mention (gc_id, seq, target_principal_id) VALUES ($1, $2, $3)", gc, seq, target);
        }

        var limited = new PostgresGcMentionSource(db.DataSource, new FixedGrantAccess(GcReadGrant.Through(2)));
        var rows = await ToListAsync(limited.ReadAsync(gc, 0, Guid.Empty, 100, ExportReaderPeople.Viewer, ct));
        Assert.Equal([1L, 2], rows.Select(m => m.Seq));

        var denied = new PostgresGcMentionSource(db.DataSource, new FixedGrantAccess(GcReadGrant.Denied));
        await Assert.ThrowsAsync<GcAccessDeniedException>(
            async () => await ToListAsync(denied.ReadAsync(gc, 0, Guid.Empty, 10, ExportReaderPeople.Viewer, ct)));
    }

    [Fact]
    public async Task Invalid_arguments_are_refused_before_any_database_work()
    {
        var ct = TestContext.Current.CancellationToken;
        // No database is opened: the argument checks and the grant check run before a command is created. A null data source is
        // enough, because the reads below throw first.
        var versions = new PostgresGcMessageVersionSource(null!, new FixedGrantAccess(GcReadGrant.Full));
        var mentions = new PostgresGcMentionSource(null!, new FixedGrantAccess(GcReadGrant.Full));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await ToListAsync(versions.ReadAsync(Guid.NewGuid(), -1, 10, ExportReaderPeople.Viewer, ct)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await ToListAsync(versions.ReadAsync(Guid.NewGuid(), 0, 0, ExportReaderPeople.Viewer, ct)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await ToListAsync(mentions.ReadAsync(Guid.NewGuid(), 0, Guid.Empty, 0, ExportReaderPeople.Viewer, ct)));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await ToListAsync(mentions.ReadAsync(Guid.NewGuid(), 0, Guid.Empty, 1, null!, ct)));
    }

    [Fact]
    public async Task Denied_viewer_is_refused_before_any_database_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var versions = new PostgresGcMessageVersionSource(null!, new FixedGrantAccess(GcReadGrant.Denied));

        await Assert.ThrowsAsync<GcAccessDeniedException>(
            async () => await ToListAsync(versions.ReadAsync(Guid.NewGuid(), 0, 10, ExportReaderPeople.Viewer, ct)));
    }
}
