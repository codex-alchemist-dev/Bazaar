using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Sync;
using Bazaar.Testing;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.GcReactions.Tests;

/// <summary>
/// A cloned test database with the core schema (<c>common</c>) and the <c>gc_reactions</c> component SQL, which has no foreign keys of
/// its own beyond the <c>gc_event_kind</c> registry row it inserts.
/// </summary>
public sealed class ReactionsDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database, capped at 12 connections.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("ReactionsDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        // Windows without BAZAAR_TEST_PG has no database; each test skips itself.
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", CancellationToken.None);

        var assembly = typeof(PgGcReactionStore).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".components.gc_reactions.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("No gc_reactions migrations are embedded in Bazaar.Server.GcReactions.");
        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            await using var cmd = DataSource.CreateCommand(await reader.ReadToEndAsync());
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<Guid> CreateGcAsync(bool archived = false)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand(
            "INSERT INTO gc (gc_id, type_key, title, created_by, archived_at) VALUES ($1, 'general', 'test', $1, CASE WHEN $2 THEN now() END)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = archived });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task AddMemberAsync(Guid gc, Guid principal, string role = "member")
    {
        await using var cmd = DataSource.CreateCommand(
            "INSERT INTO gc_member (gc_id, principal_id, role, joined_seq, left_seq) VALUES ($1, $2, $3, 0, NULL)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principal });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}

/// <summary>Reactions over the real GC log: the writer, the viewer rules and Postgres.</summary>
public sealed class PostgresReactionTests(ReactionsDb db) : IClassFixture<ReactionsDb>
{
    private static readonly JsonWireCodec _codec = new();
    private static readonly Guid _ada = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid _bob = Guid.Parse("00000000-0000-7000-8000-00000000000b");

    private sealed class NoNotifier : IGcNotifier
    {
        public void Notify(Guid gcId, long seq)
        {
        }
    }

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private (GcReactHandler React, GcReactionsHandler Reactions, GcTypingHandler Typing, PgGcReactionStore Store) Handlers()
    {
        var clock = new FakeClock();
        var access = new GcReadAccess(db.DataSource);
        var store = new PgGcReactionStore(db.DataSource);
        var broadcaster = new GcLiveBroadcaster(new EmptyDirectory(), new SyncOptions(), clock);
        var options = new GcReactionsOptions();
        return (
            new GcReactHandler(new GcLogWriter(db.DataSource, new NoNotifier()), access, store, broadcaster, new SlidingRateLimiter(clock, TimeSpan.FromMinutes(1)), options),
            new GcReactionsHandler(access, store),
            new GcTypingHandler(access, new TypingTracker(clock), broadcaster, options),
            store);
    }

    private sealed class EmptyDirectory : IWireConnectionDirectory
    {
        public IReadOnlyCollection<IWireConnection> Connections => [];
    }

    private async Task<(Guid Gc, long MessageSeq)> GcWithMessageAsync(Guid author)
    {
        var gc = await db.CreateGcAsync();
        await db.AddMemberAsync(gc, author);
        await db.AddMemberAsync(gc, _bob);
        var writer = new GcLogWriter(db.DataSource, new NoNotifier());
        var appended = await writer.AppendAsync(
            new AppendRequest(gc, GcEventKinds.Message, AuthorKind.Human, author, SourceTrust.MemberHuman, "m-1")
            {
                Message = await new PlaintextGcBodyCodec().SealAsync(author, "hello", default),
            },
            CancellationToken.None);
        return (gc, appended.Seq);
    }

    private static WireRequestContext Context(string type, Guid who, object payload, string id, string kind = "human") =>
        new(new Connection(new PrincipalRef(who, kind)),
            new Envelope(1, type, EnvelopeKind.Request, id, null, null, null, null, _codec.EncodePayload(payload)));

    private static T Decode<T>(WireReply reply) =>
        _codec.DecodePayload<T>(new Envelope(1, "reply", EnvelopeKind.Response, null, null, null, null, null, reply.Payload));

    [Fact]
    public async Task The_migration_registers_the_reaction_kind_and_its_partial_index()
    {
        SkipWithoutDatabase();

        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event_kind WHERE kind = 'reaction'"));
        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'gc_event_reaction_target_idx'"));
    }

    [Fact]
    public async Task Add_and_remove_are_log_events_with_gap_free_seqs_and_the_fold_follows()
    {
        SkipWithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var (gc, message) = await GcWithMessageAsync(_ada);
        var h = Handlers();

        var added = Decode<GcReactResult>(await h.React.HandleAsync(Context(WireTypes.GcReact, _ada, new GcReact(gc, message, "👍", true), "r-1"), ct));
        var other = Decode<GcReactResult>(await h.React.HandleAsync(Context(WireTypes.GcReact, _bob, new GcReact(gc, message, "👍", true), "r-2"), ct));
        var state = Decode<GcReactionsReply>(await h.Reactions.HandleAsync(Context(WireTypes.GcReactions, _ada, new GcReactionsRequest(gc, null), "r-3"), ct));

        Assert.Equal(new GcReactResult(message + 1, true), added);
        Assert.Equal(new GcReactResult(message + 2, true), other);
        var group = Assert.Single(state.Items);
        Assert.Equal((message, "👍", 2), (group.TargetN, group.Emoji, group.By.Count));

        var removed = Decode<GcReactResult>(await h.React.HandleAsync(Context(WireTypes.GcReact, _ada, new GcReact(gc, message, "👍", false), "r-4"), ct));
        var after = Decode<GcReactionsReply>(await h.Reactions.HandleAsync(Context(WireTypes.GcReactions, _ada, new GcReactionsRequest(gc, null), "r-5"), ct));

        Assert.Equal(new GcReactResult(message + 3, true), removed);
        Assert.Equal([_bob], Assert.Single(after.Items).By);
        // The log keeps every event: three reactions, none deleted, and the head moved only for real changes.
        Assert.Equal(3L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1 AND kind = 'reaction'", gc));
        Assert.Equal(message + 3, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
    }

    [Fact]
    public async Task The_reaction_row_has_the_documented_shape_and_the_log_refuses_to_change_it()
    {
        SkipWithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var (gc, message) = await GcWithMessageAsync(_ada);
        var h = Handlers();
        await h.React.HandleAsync(Context(WireTypes.GcReact, _ada, new GcReact(gc, message, "🎉", true), "r-1"), ct);

        Assert.Equal(message, await db.ScalarAsync<long>("SELECT target_seq FROM gc_event WHERE gc_id = $1 AND kind = 'reaction'", gc));
        Assert.True(await db.ScalarAsync<bool>(
            "SELECT message_id IS NULL AND author_kind = 'human' AND source_trust = 'member-human' FROM gc_event WHERE gc_id = $1 AND kind = 'reaction'", gc));
        Assert.Equal("🎉", await db.ScalarAsync<string>("SELECT payload ->> 'e' FROM gc_event WHERE gc_id = $1 AND kind = 'reaction'", gc));
        await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = db.DataSource.CreateCommand("DELETE FROM gc_event WHERE gc_id = $1 AND kind = 'reaction'");
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
            await cmd.ExecuteNonQueryAsync(ct);
        });
    }

    [Fact]
    public async Task A_repeat_add_a_remove_with_nothing_to_remove_and_typing_write_no_events()
    {
        SkipWithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var (gc, message) = await GcWithMessageAsync(_ada);
        var h = Handlers();
        await h.React.HandleAsync(Context(WireTypes.GcReact, _ada, new GcReact(gc, message, "👍", true), "r-1"), ct);
        var head = await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc);

        var again = Decode<GcReactResult>(await h.React.HandleAsync(Context(WireTypes.GcReact, _ada, new GcReact(gc, message, "👍", true), "r-2"), ct));
        var nothing = Decode<GcReactResult>(await h.React.HandleAsync(Context(WireTypes.GcReact, _bob, new GcReact(gc, message, "👍", false), "r-3"), ct));
        var typing = Decode<GcTypingResult>(await h.Typing.HandleAsync(Context(WireTypes.GcTyping, _ada, new GcTyping(gc, true), "t-1"), ct));

        Assert.Equal(new GcReactResult(head, false), again);
        Assert.Equal(new GcReactResult(0, false), nothing);
        Assert.True(typing.Accepted);
        Assert.Equal(head, await db.ScalarAsync<long>("SELECT head_seq FROM gc WHERE gc_id = $1", gc));
    }

    [Fact]
    public async Task Only_members_who_may_write_can_react_or_type_and_the_answer_is_not_found()
    {
        SkipWithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var (gc, message) = await GcWithMessageAsync(_ada);
        var observer = Guid.CreateVersion7();
        await db.AddMemberAsync(gc, observer, "observer");
        var outsider = Guid.CreateVersion7();
        var h = Handlers();

        foreach (var who in new[] { observer, outsider })
        {
            var react = await h.React.HandleAsync(Context(WireTypes.GcReact, who, new GcReact(gc, message, "👍", true), $"r-{who}"), ct);
            var typing = await h.Typing.HandleAsync(Context(WireTypes.GcTyping, who, new GcTyping(gc, true), $"t-{who}"), ct);

            Assert.Equal(WireErrorCodes.NotFound, react.Error!.Code);
            Assert.Equal(WireErrorCodes.NotFound, typing.Error!.Code);
        }

        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1 AND kind = 'reaction'", gc));
    }

    [Fact]
    public async Task An_observer_and_an_outsider_cannot_read_more_than_the_viewer_rules_allow()
    {
        SkipWithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var (gc, _) = await GcWithMessageAsync(_ada);
        var outsider = Guid.CreateVersion7();
        var h = Handlers();

        var reply = await h.Reactions.HandleAsync(Context(WireTypes.GcReactions, outsider, new GcReactionsRequest(gc, null), "q-1"), ct);

        Assert.Equal(WireErrorCodes.NotFound, reply.Error!.Code);
    }

    [Fact]
    public async Task A_target_that_is_not_a_message_is_not_found()
    {
        SkipWithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var (gc, message) = await GcWithMessageAsync(_ada);
        var h = Handlers();
        var firstReaction = Decode<GcReactResult>(await h.React.HandleAsync(Context(WireTypes.GcReact, _ada, new GcReact(gc, message, "👍", true), "r-1"), ct));

        var onReaction = await h.React.HandleAsync(Context(WireTypes.GcReact, _ada, new GcReact(gc, firstReaction.N, "👍", true), "r-2"), ct);
        var beyondHead = await h.React.HandleAsync(Context(WireTypes.GcReact, _ada, new GcReact(gc, 999, "👍", true), "r-3"), ct);

        Assert.Equal(WireErrorCodes.NotFound, onReaction.Error!.Code);
        Assert.Equal(WireErrorCodes.NotFound, beyondHead.Error!.Code);
    }

    [Fact]
    public async Task An_archived_gc_takes_no_reactions()
    {
        SkipWithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var (gc, message) = await GcWithMessageAsync(_ada);
        await using (var archive = db.DataSource.CreateCommand("UPDATE gc SET archived_at = now() WHERE gc_id = $1"))
        {
            archive.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
            await archive.ExecuteNonQueryAsync(ct);
        }

        var reply = await Handlers().React.HandleAsync(Context(WireTypes.GcReact, _ada, new GcReact(gc, message, "👍", true), "r-1"), ct);

        Assert.False(reply.IsOk);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1 AND kind = 'reaction'", gc));
    }

    [Fact]
    public async Task Concurrent_reactions_from_many_people_keep_the_seq_gap_free()
    {
        SkipWithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var (gc, message) = await GcWithMessageAsync(_ada);
        var people = Enumerable.Range(0, 12).Select(_ => Guid.CreateVersion7()).ToList();
        foreach (var person in people)
            await db.AddMemberAsync(gc, person);
        var h = Handlers();

        await Task.WhenAll(people.Select(p =>
            h.React.HandleAsync(Context(WireTypes.GcReact, p, new GcReact(gc, message, "🔥", true), $"c-{p}"), ct).AsTask()));

        Assert.Equal(12L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1 AND kind = 'reaction'", gc));
        Assert.Equal(message + 12, await db.ScalarAsync<long>("SELECT max(seq) FROM gc_event WHERE gc_id = $1", gc));
        Assert.Equal(message + 12, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1", gc));
        Assert.Equal(12, Assert.Single((await h.Store.ReadAsync(gc, null, null, ct)).GroupBy(r => r.Emoji)).Count());
    }

    private sealed class Connection(PrincipalRef principal) : IWireConnection
    {
        public Guid ConnectionId { get; } = Guid.NewGuid();

        public PrincipalRef Principal => principal;

        public string DeviceId => "test-device";

        public string DeviceClass => "test";

        public IWireCodec Codec => _codec;

        public IReadOnlyList<StreamCursor> HelloCursors => [];

        public ISubscriptionSession? Subscriptions => null;

        public CancellationToken Closed => CancellationToken.None;

        public bool TryEnqueueEvent(Envelope envelope) => true;

        public bool TryEnqueueLagged(Lagged lagged) => true;

        public ValueTask CloseAsync(int closeCode, string reason) => ValueTask.CompletedTask;
    }
}
