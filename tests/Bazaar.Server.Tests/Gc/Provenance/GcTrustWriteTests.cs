using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Provenance;

/// <summary>The trust hook on the real append path: derived values reach <c>gc_event</c> (docs/design/01, 04).</summary>
public sealed class GcTrustWriteTests(GcDb db) : IClassFixture<GcDb>
{
    private static AppendRequest Request(Guid gc, AuthorKind authorKind, Guid author, SourceTrust trust, string key) =>
        new(gc, GcEventKinds.Message, authorKind, author, trust, key)
        {
            Message = new MessageBody(Encoding.UTF8.GetBytes("hi"), Guid.CreateVersion7()),
        };

    private async Task<(bool Tainted, string? Sources)> StoredTaintAsync(Guid gc, long seq)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT tainted_at_write, taint_sources::text FROM gc_event WHERE gc_id = $1 AND seq = $2");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    [Fact]
    public async Task External_and_agent_events_are_tagged_on_write_without_caller_flags()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        var ct = TestContext.Current.CancellationToken;
        var author = Guid.CreateVersion7();

        await writer.AppendAsync(Request(gc, AuthorKind.ExternalBridge, author, SourceTrust.External, "ext"), ct);
        await writer.AppendAsync(Request(gc, AuthorKind.Agent, author, SourceTrust.Agent, "clean"), ct);
        await writer.AppendAsync(
            Request(gc, AuthorKind.Agent, author, SourceTrust.Agent, "soft") with { TaintSourcesJson = """["ext-1"]""" }, ct);
        await writer.AppendAsync(Request(gc, AuthorKind.Owner, author, SourceTrust.Owner, "owner"), ct);

        Assert.Equal((true, null), await StoredTaintAsync(gc, 1));
        Assert.Equal((false, null), await StoredTaintAsync(gc, 2));
        Assert.Equal((true, """["ext-1"]"""), await StoredTaintAsync(gc, 3));
        Assert.Equal((false, null), await StoredTaintAsync(gc, 4));
    }

    [Fact]
    public async Task Explicit_taint_from_the_caller_is_stored_as_given()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        var ct = TestContext.Current.CancellationToken;
        var author = Guid.CreateVersion7();

        await writer.AppendAsync(
            Request(gc, AuthorKind.ExternalBridge, author, SourceTrust.External, "ext-off") with { TaintedAtWrite = false }, ct);

        Assert.Equal((false, null), await StoredTaintAsync(gc, 1));
    }

    [Fact]
    public async Task A_rejected_lineage_writes_nothing_and_leaves_the_head_alone()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        var ct = TestContext.Current.CancellationToken;
        var author = Guid.CreateVersion7();

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(
            Request(gc, AuthorKind.Agent, author, SourceTrust.Agent, "bad") with { TaintSourcesJson = "{}" }, ct));

        await writer.AppendAsync(Request(gc, AuthorKind.Owner, author, SourceTrust.Owner, "next"), ct);
        Assert.Equal((false, null), await StoredTaintAsync(gc, 1));
    }
}
