using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Modules.Health;
using Bazaar.Server.Souls;
using Bazaar.Server.Tests.Gc;
using Bazaar.Souls.Abstractions;
using Npgsql;

namespace Bazaar.Server.Tests.Souls.Binding;

/// <summary>
/// The agent-to-Soul mapping, the Soul stamp on the GC log and the stamp reconciliation, on a real database
/// (docs/design/07 "Versioning", docs/design/01; migrations <c>souls/0002</c> and <c>gc/0001</c>).
/// </summary>
public sealed class SoulBindingTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly string _sha = new('a', 64);

    private PostgresSoulBindings Bindings => new(db.DataSource);

    private async Task<Guid> SoulWithVersionsAsync(int versions)
    {
        var soul = Guid.CreateVersion7();
        var service = new SoulVersioningService(db.DataSource);
        for (var i = 0; i < versions; i++)
            await service.AppendAsync(new SoulVersionAppend(soul, i, _sha, "{}", "tester", "test version", null), TestContext.Current.CancellationToken);
        return soul;
    }

    private static AppendRequest AgentMessage(Guid gc, Guid agent, string key, SoulRef? soul, AuthorKind kind = AuthorKind.Agent) =>
        new(gc, GcEventKinds.Message, kind, agent, SourceTrust.Agent, key)
        {
            Message = new MessageBody(Encoding.UTF8.GetBytes("hello"), Guid.CreateVersion7()),
            Soul = soul,
        };

    private async Task<object?[]> RowAsync(string sql, params object[] args)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        var row = new object[reader.FieldCount];
        reader.GetValues(row);
        return row.Select(v => v is DBNull ? null : v).ToArray();
    }

    private sealed class NullNotifier : IGcNotifier
    {
        public void Notify(Guid gcId, long seq)
        {
        }
    }

    [Fact]
    public async Task An_unbound_agent_resolves_to_nothing()
    {
        GcTestSkip.WithoutDatabase();
        Assert.Null(await Bindings.ResolveAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_bound_agent_resolves_to_the_head_of_its_soul_and_follows_new_versions()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = Guid.CreateVersion7();
        var soul = await SoulWithVersionsAsync(2);
        await Bindings.BindAsync(agent, soul, "hired", ct);

        Assert.Equal(new SoulRef(soul, 2), await Bindings.ResolveAsync(agent, ct));

        await new SoulVersioningService(db.DataSource).AppendAsync(new SoulVersionAppend(soul, 2, _sha, "{}", "tester", "tuned", null), ct);

        Assert.Equal(new SoulRef(soul, 3), await Bindings.ResolveAsync(agent, ct));
    }

    [Fact]
    public async Task Binding_the_same_soul_twice_writes_one_row()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = Guid.CreateVersion7();
        var soul = await SoulWithVersionsAsync(1);
        await Bindings.BindAsync(agent, soul, "hired", ct);
        await Bindings.BindAsync(agent, soul, "again", ct);

        Assert.Equal(1L, (await RowAsync("SELECT count(*) FROM soul_agent_binding WHERE agent_id = $1", agent))[0]);
    }

    [Fact]
    public async Task A_rebind_changes_new_posts_but_old_stamps_stay_valid()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = Guid.CreateVersion7();
        var first = await SoulWithVersionsAsync(1);
        var second = await SoulWithVersionsAsync(2);
        await Bindings.BindAsync(agent, first, "hired", ct);
        await Bindings.BindAsync(agent, second, "transferred", ct);

        Assert.Equal(new SoulRef(second, 2), await Bindings.ResolveAsync(agent, ct));
        Assert.True(await Bindings.IsValidStampAsync(agent, new SoulRef(first, 1), ct));
        Assert.True(await Bindings.IsValidStampAsync(agent, new SoulRef(second, 1), ct));
        Assert.Equal(2L, (await RowAsync("SELECT count(*) FROM soul_agent_binding WHERE agent_id = $1", agent))[0]);
    }

    [Fact]
    public async Task A_stamp_is_invalid_for_another_agent_a_missing_version_or_an_empty_reference()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = Guid.CreateVersion7();
        var soul = await SoulWithVersionsAsync(1);
        await Bindings.BindAsync(agent, soul, "hired", ct);

        Assert.False(await Bindings.IsValidStampAsync(Guid.CreateVersion7(), new SoulRef(soul, 1), ct));
        Assert.False(await Bindings.IsValidStampAsync(agent, new SoulRef(soul, 2), ct));
        Assert.False(await Bindings.IsValidStampAsync(agent, new SoulRef(Guid.CreateVersion7(), 1), ct));
        Assert.False(await Bindings.IsValidStampAsync(agent, default, ct));
    }

    [Fact]
    public async Task A_soul_with_no_versions_cannot_be_bound()
    {
        GcTestSkip.WithoutDatabase();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Bindings.BindAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), "hired", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_retired_agent_resolves_to_nothing_keeps_its_history_and_cannot_be_rebound()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = Guid.CreateVersion7();
        var soul = await SoulWithVersionsAsync(1);
        await Bindings.BindAsync(agent, soul, "hired", ct);
        await Bindings.RetireAsync(agent, "retired by vote", ct);
        await Bindings.RetireAsync(agent, "again", ct);

        Assert.Null(await Bindings.ResolveAsync(agent, ct));
        Assert.True(await Bindings.IsValidStampAsync(agent, new SoulRef(soul, 1), ct));
        Assert.Equal(2L, (await RowAsync("SELECT count(*) FROM soul_agent_binding WHERE agent_id = $1", agent))[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Bindings.BindAsync(agent, soul, "back", ct));
    }

    [Fact]
    public async Task A_stamped_agent_message_stores_the_stamp_on_the_event_and_the_message_version()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = Guid.CreateVersion7();
        var soul = await SoulWithVersionsAsync(2);
        await Bindings.BindAsync(agent, soul, "hired", ct);
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new NullNotifier(), soulBindings: Bindings);

        var result = await writer.AppendAsync(AgentMessage(gc, agent, "k1", new SoulRef(soul, 2)), ct);

        var ev = await RowAsync("SELECT soul_id, soul_version FROM gc_event WHERE gc_id = $1 AND seq = $2", gc, result.Seq);
        var version = await RowAsync("SELECT soul_id, soul_version FROM gc_message_version WHERE gc_id = $1 AND seq = $2", gc, result.Seq);
        Assert.Equal([soul, 2], ev);
        Assert.Equal([soul, 2], version);
    }

    [Fact]
    public async Task A_repeat_of_a_stamped_post_returns_the_original_event_and_keeps_its_stamp()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = Guid.CreateVersion7();
        var soul = await SoulWithVersionsAsync(2);
        await Bindings.BindAsync(agent, soul, "hired", ct);
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new NullNotifier(), soulBindings: Bindings);
        var first = await writer.AppendAsync(AgentMessage(gc, agent, "k1", new SoulRef(soul, 1)), ct);

        await new SoulVersioningService(db.DataSource).AppendAsync(new SoulVersionAppend(soul, 2, _sha, "{}", "tester", "tuned", null), ct);
        var repeat = await writer.AppendAsync(AgentMessage(gc, agent, "k1", new SoulRef(soul, 3)), ct);

        Assert.True(repeat.Duplicate);
        Assert.Equal(first.EventId, repeat.EventId);
        var ev = await RowAsync("SELECT soul_version FROM gc_event WHERE gc_id = $1 AND seq = $2", gc, first.Seq);
        Assert.Equal([1], ev);
    }

    [Fact]
    public async Task A_stamp_that_is_not_a_real_bound_version_is_refused_and_nothing_is_written()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = Guid.CreateVersion7();
        var soul = await SoulWithVersionsAsync(1);
        await Bindings.BindAsync(agent, soul, "hired", ct);
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new NullNotifier(), soulBindings: Bindings);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(AgentMessage(gc, agent, "k1", new SoulRef(soul, 9)), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(AgentMessage(gc, Guid.CreateVersion7(), "k2", new SoulRef(soul, 1)), ct));

        Assert.Equal(0L, (await RowAsync("SELECT count(*) FROM gc_event WHERE gc_id = $1", gc))[0]);
    }

    [Fact]
    public async Task Only_an_agent_author_may_carry_a_stamp()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var author = Guid.CreateVersion7();
        var soul = await SoulWithVersionsAsync(1);
        await Bindings.BindAsync(author, soul, "hired", ct);
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new NullNotifier(), soulBindings: Bindings);

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendAsync(AgentMessage(gc, author, "k1", new SoulRef(soul, 1), AuthorKind.Human), ct));
    }

    [Fact]
    public async Task A_writer_without_bindings_refuses_a_stamp_instead_of_trusting_it()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new NullNotifier());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.AppendAsync(AgentMessage(gc, Guid.CreateVersion7(), "k1", new SoulRef(Guid.CreateVersion7(), 1)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_database_rejects_a_half_set_stamp_and_a_stamp_on_a_human_event()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        const string insert = """
            INSERT INTO gc_event (gc_id, seq, event_id, kind, author_kind, author_id, source_trust, client_msg_id, soul_id, soul_version)
            VALUES ($1, $2, $3, 'structured', $4, $5, 'agent', $6, $7, $8)
            """;

        async Task InsertAsync(long seq, string authorKind, Guid? soul, int? version)
        {
            await using var cmd = db.DataSource.CreateCommand(insert);
            cmd.Parameters.Add(new NpgsqlParameter { Value = gc });
            cmd.Parameters.Add(new NpgsqlParameter { Value = seq });
            cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = authorKind });
            cmd.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            cmd.Parameters.Add(new NpgsqlParameter { Value = $"k{seq}" });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)soul ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)version ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(1, "agent", Guid.CreateVersion7(), null));
        await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(2, "agent", null, 1));
        await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(3, "human", Guid.CreateVersion7(), 1));
        await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(4, "agent", Guid.CreateVersion7(), 0));
    }

    [Fact]
    public async Task The_reconciliation_finds_a_stamp_whose_version_no_longer_exists_and_health_fails_on_it()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var agent = Guid.CreateVersion7();
        var soul = await SoulWithVersionsAsync(2);
        await Bindings.BindAsync(agent, soul, "hired", ct);
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new NullNotifier(), soulBindings: Bindings);
        await writer.AppendAsync(AgentMessage(gc, agent, "k1", new SoulRef(soul, 2)), ct);
        var reconciler = new SoulStampReconciler(db.DataSource);
        var health = new SoulBindingsHealth(reconciler);

        Assert.DoesNotContain(new SoulRef(soul, 2), await reconciler.FindDanglingAsync(100, ct));
        Assert.Equal(ModuleHealthStatus.Healthy, (await health.SelfTestAsync(ModuleSelfTestScope.Quick, ct)).Status);

        await using (var delete = db.DataSource.CreateCommand("DELETE FROM soul_versions WHERE soul_id = $1 AND version = 2"))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = soul });
            await delete.ExecuteNonQueryAsync(ct);
        }

        Assert.Contains(new SoulRef(soul, 2), await reconciler.FindDanglingAsync(100, ct));
        Assert.Equal(ModuleHealthStatus.Failing, (await health.SelfTestAsync(ModuleSelfTestScope.Quick, ct)).Status);
    }
}
