using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.TimeMachine.Tests;

public sealed class PostgresForkLinkStoreTests
{
    private const string _skipReason = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    private static readonly DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static ForkLink Link(Guid sourceGcId, long seq, Guid requestId, string? commit = "abc123", int minutes = 0) =>
        new(Guid.NewGuid(), sourceGcId, seq, commit, requestId, _now.AddMinutes(minutes));

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(TimeMachineComponent).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM fork_link");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task Append_then_find_round_trips_a_link()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("timemachine", MigrateAsync, ct);
        var store = new PostgresForkLinkStore(db.DataSource);
        var link = Link(Guid.NewGuid(), 42, Guid.NewGuid());

        var (appended, created) = await store.AppendAsync(link, ct);
        var found = await store.FindAsync(link.SourceGcId, link.SourceSeq, link.RequestId, ct);

        Assert.True(created);
        Assert.Equal(link, appended);
        Assert.Equal(link, found);
    }

    [Fact]
    public async Task A_null_vault_commit_round_trips()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("timemachine", MigrateAsync, ct);
        var store = new PostgresForkLinkStore(db.DataSource);
        var link = Link(Guid.NewGuid(), 0, Guid.NewGuid(), commit: null);

        await store.AppendAsync(link, ct);

        Assert.Equal(link, await store.FindAsync(link.SourceGcId, link.SourceSeq, link.RequestId, ct));
    }

    [Fact]
    public async Task Find_returns_null_for_an_unknown_key_and_for_a_partial_key_match()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("timemachine", MigrateAsync, ct);
        var store = new PostgresForkLinkStore(db.DataSource);
        var link = Link(Guid.NewGuid(), 7, Guid.NewGuid());
        await store.AppendAsync(link, ct);

        Assert.Null(await store.FindAsync(Guid.NewGuid(), 7, link.RequestId, ct));
        Assert.Null(await store.FindAsync(link.SourceGcId, 8, link.RequestId, ct));
        Assert.Null(await store.FindAsync(link.SourceGcId, 7, Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task A_repeat_on_the_same_key_returns_the_first_link_and_writes_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("timemachine", MigrateAsync, ct);
        var store = new PostgresForkLinkStore(db.DataSource);
        var source = Guid.NewGuid();
        var request = Guid.NewGuid();
        var first = Link(source, 5, request, "first", 0);
        var second = Link(source, 5, request, "second", 10);

        var (firstResult, firstCreated) = await store.AppendAsync(first, ct);
        var (secondResult, secondCreated) = await store.AppendAsync(second, ct);

        Assert.True(firstCreated);
        Assert.False(secondCreated);
        Assert.Equal(first, firstResult);
        Assert.Equal(first, secondResult);
        Assert.NotEqual(second.NewGcId, secondResult.NewGcId);
        Assert.Equal(first, await store.FindAsync(source, 5, request, ct));
        Assert.Equal(1, await CountAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task A_different_request_id_or_effective_seq_creates_a_separate_link()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("timemachine", MigrateAsync, ct);
        var store = new PostgresForkLinkStore(db.DataSource);
        var source = Guid.NewGuid();
        var request = Guid.NewGuid();

        var (_, a) = await store.AppendAsync(Link(source, 5, request), ct);
        var (_, otherRequest) = await store.AppendAsync(Link(source, 5, Guid.NewGuid()), ct);
        var (_, otherSeq) = await store.AppendAsync(Link(source, 6, request), ct);
        var (_, otherSource) = await store.AppendAsync(Link(Guid.NewGuid(), 5, request), ct);

        Assert.True(a);
        Assert.True(otherRequest);
        Assert.True(otherSeq);
        Assert.True(otherSource);
        Assert.Equal(4, await CountAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task Concurrent_appends_of_the_same_key_create_exactly_one_link()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("timemachine", MigrateAsync, ct);
        var store = new PostgresForkLinkStore(db.DataSource);
        var source = Guid.NewGuid();
        var request = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.AppendAsync(Link(source, 3, request, $"c{i}"), ct)));

        Assert.Single(results, r => r.Created);
        Assert.Single(results.Select(r => r.Link.NewGcId).Distinct());
        Assert.Equal(1, await CountAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task The_store_is_append_only_so_an_existing_row_is_never_rewritten()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("timemachine", MigrateAsync, ct);
        var store = new PostgresForkLinkStore(db.DataSource);
        var source = Guid.NewGuid();
        var first = Link(source, 1, Guid.NewGuid(), "v1");
        await store.AppendAsync(first, ct);

        // Reusing the new GC id under another key is a primary-key violation, never an overwrite.
        var clash = first with { SourceSeq = 2, RequestId = Guid.NewGuid(), VaultCommit = "v2" };
        await Assert.ThrowsAsync<PostgresException>(() => store.AppendAsync(clash, ct));

        Assert.Equal(first, await store.FindAsync(source, 1, first.RequestId, ct));
        Assert.Null(await store.FindAsync(source, 2, clash.RequestId, ct));
        Assert.Equal(1, await CountAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task List_by_source_returns_only_that_source_in_creation_order()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("timemachine", MigrateAsync, ct);
        var store = new PostgresForkLinkStore(db.DataSource);
        var source = Guid.NewGuid();
        var late = Link(source, 9, Guid.NewGuid(), "late", 30);
        var early = Link(source, 2, Guid.NewGuid(), "early", 0);
        var middle = Link(source, 5, Guid.NewGuid(), "middle", 10);
        var other = Link(Guid.NewGuid(), 2, Guid.NewGuid());
        foreach (var l in new[] { late, early, middle, other })
            await store.AppendAsync(l, ct);

        var listed = await store.ListBySourceAsync(source, ct);

        Assert.Equal([early, middle, late], listed);
        Assert.Empty(await store.ListBySourceAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task Append_rejects_a_null_link()
    {
        var store = new PostgresForkLinkStore(null!);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AppendAsync(null!, TestContext.Current.CancellationToken));
    }
}
