using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Constellation;

/// <summary>Constellation projection views and <c>constellation_visible_nodes</c> on real PostgreSQL (migration 0019; doc 19 Testing).</summary>
public sealed class ConstellationViewTests(ConstellationDb db) : IClassFixture<ConstellationDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource ds, string sql, params object[] args)
    {
        await using var cmd = ds.CreateCommand(sql);
        foreach (var arg in args)
            cmd.Parameters.AddWithValue(arg);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<HashSet<string>> VisibleAsync(NpgsqlDataSource ds, Guid viewer, bool isOwner)
    {
        await using var cmd = ds.CreateCommand("SELECT node FROM constellation_visible_nodes($1, $2) AS node");
        cmd.Parameters.AddWithValue(viewer);
        cmd.Parameters.AddWithValue(isOwner);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var nodes = new HashSet<string>();
        while (await reader.ReadAsync(Ct))
            nodes.Add(reader.GetString(0));
        return nodes;
    }

    [Fact]
    public async Task Projection_views_exist_and_the_runtime_role_can_only_read_them()
    {
        SkipWithoutDatabase();
        var ds = db.DataSource;
        Assert.Equal(8, await ScalarAsync<long>(ds, "SELECT count(*) FROM information_schema.views WHERE starts_with(table_name, 'constellation_')"));
        Assert.False(await ScalarAsync<bool>(ds, "SELECT has_table_privilege('bazaar_app', 'constellation_doc_nodes', 'SELECT')"));
        Assert.True(await ScalarAsync<bool>(ds, "SELECT has_function_privilege('bazaar_app', 'constellation_node_rows(uuid, boolean)', 'EXECUTE')"));
        Assert.True(await ScalarAsync<bool>(ds, "SELECT has_function_privilege('bazaar_app', 'constellation_edge_rows(uuid, boolean)', 'EXECUTE')"));
        Assert.False(await ScalarAsync<bool>(ds, "SELECT has_table_privilege('bazaar_app', 'constellation_doc_nodes', 'INSERT')"));
        Assert.True(await ScalarAsync<bool>(ds, "SELECT has_function_privilege('bazaar_app', 'constellation_visible_nodes(uuid, boolean)', 'EXECUTE')"));
    }

    [Fact]
    public async Task Unresolved_link_yields_a_ghost_node_and_an_edge_while_resolved_links_stay_out_of_ghosts()
    {
        SkipWithoutDatabase();
        var ds = db.DataSource;
        var s = await ConstellationSeed.InsertAsync(ds);
        var ghost = ConstellationSeed.GhostId(s.MissingTarget);

        Assert.Contains(ghost, await VisibleAsync(ds, s.Outsider, isOwner: false));
        Assert.Equal(1, await ScalarAsync<long>(ds, "SELECT count(*) FROM constellation_ghost_nodes WHERE node_id = $1", ghost));
        Assert.Equal(1, await ScalarAsync<long>(ds,
            "SELECT count(*) FROM constellation_ghost_edges WHERE from_node_id = $1 AND to_node_id = $2", s.PublicDoc.ToString(), ghost));
        Assert.Equal(0, await ScalarAsync<long>(ds, "SELECT count(*) FROM constellation_link_edges WHERE to_node_id = $1", ghost));
        Assert.Equal(1, await ScalarAsync<long>(ds,
            "SELECT count(*) FROM constellation_link_edges WHERE from_node_id = $1 AND to_node_id = $2", s.PublicDoc.ToString(), s.PlanDoc.ToString()));
    }

    [Fact]
    public async Task Ghost_node_from_a_private_note_is_hidden_from_other_agents()
    {
        SkipWithoutDatabase();
        var ds = db.DataSource;
        var s = await ConstellationSeed.InsertAsync(ds);
        var ghost = ConstellationSeed.GhostId(s.PrivateTarget);

        Assert.DoesNotContain(ghost, await VisibleAsync(ds, s.AgentB, isOwner: false));
        Assert.DoesNotContain(ghost, await VisibleAsync(ds, s.Outsider, isOwner: false));
        Assert.Contains(ghost, await VisibleAsync(ds, s.AgentA, isOwner: false));
        Assert.Contains(ghost, await VisibleAsync(ds, s.Owner, isOwner: true));
    }

    [Fact]
    public async Task Thinking_gc_produces_no_node_and_no_edge()
    {
        SkipWithoutDatabase();
        var ds = db.DataSource;
        var s = await ConstellationSeed.InsertAsync(ds);
        var thinking = s.ThinkingGc.ToString();

        Assert.Equal(0, await ScalarAsync<long>(ds, "SELECT count(*) FROM constellation_gc_nodes WHERE node_id = $1", thinking));
        Assert.Equal(0, await ScalarAsync<long>(ds, "SELECT count(*) FROM constellation_gc_edges WHERE from_node_id = $1 OR to_node_id = $1", thinking));
        Assert.DoesNotContain(thinking, await VisibleAsync(ds, s.Owner, isOwner: true));
        Assert.DoesNotContain(thinking, await VisibleAsync(ds, s.AgentA, isOwner: false));
        Assert.Contains(s.GeneralGc.ToString(), await VisibleAsync(ds, s.Human, isOwner: false));
    }

    [Fact]
    public async Task Another_agents_memory_is_absent_for_other_viewers_and_present_for_the_owner()
    {
        SkipWithoutDatabase();
        var ds = db.DataSource;
        var s = await ConstellationSeed.InsertAsync(ds);
        var memory = s.MemoryDoc.ToString();

        Assert.DoesNotContain(memory, await VisibleAsync(ds, s.AgentB, isOwner: false));
        Assert.DoesNotContain(memory, await VisibleAsync(ds, s.Outsider, isOwner: false));
        Assert.DoesNotContain(memory, await VisibleAsync(ds, s.Human, isOwner: false));
        Assert.Contains(memory, await VisibleAsync(ds, s.AgentA, isOwner: false));
        Assert.Contains(memory, await VisibleAsync(ds, s.Owner, isOwner: true));
        Assert.Contains(s.PublicDoc.ToString(), await VisibleAsync(ds, s.AgentB, isOwner: false));
    }

    [Fact]
    public async Task Dm_node_is_visible_to_members_and_the_owner_flag_only()
    {
        SkipWithoutDatabase();
        var ds = db.DataSource;
        var s = await ConstellationSeed.InsertAsync(ds);
        var dm = s.DmGc.ToString();

        Assert.DoesNotContain(dm, await VisibleAsync(ds, s.Outsider, isOwner: false));
        Assert.DoesNotContain(dm, await VisibleAsync(ds, s.AgentB, isOwner: false));
        Assert.Contains(dm, await VisibleAsync(ds, s.Human, isOwner: false));
        Assert.Contains(dm, await VisibleAsync(ds, s.AgentA, isOwner: false));
        Assert.Contains(dm, await VisibleAsync(ds, s.Owner, isOwner: true));
        // The owner's rights come from the is_owner flag, not from the principal id: the owner is not a DM member.
        Assert.DoesNotContain(dm, await VisibleAsync(ds, s.Owner, isOwner: false));
    }

    [Fact]
    public async Task Task_with_a_blocker_edge_projects_for_members_and_the_owner()
    {
        SkipWithoutDatabase();
        var ds = db.DataSource;
        var s = await ConstellationSeed.InsertAsync(ds);
        var blocker = s.BlockerTask.ToString();
        var blocked = s.BlockedTask.ToString();

        Assert.Equal(1, await ScalarAsync<long>(ds,
            "SELECT count(*) FROM constellation_task_edges WHERE from_node_id = $1 AND to_node_id = $2 AND kind = 'blocks'", blocker, blocked));
        Assert.Equal(2, await ScalarAsync<long>(ds,
            "SELECT count(*) FROM constellation_task_nodes WHERE node_id IN ($1, $2)", blocker, blocked));

        var owner = await VisibleAsync(ds, s.Owner, isOwner: true);
        Assert.Contains(blocker, owner);
        Assert.Contains(blocked, owner);

        var human = await VisibleAsync(ds, s.Human, isOwner: false);
        Assert.Contains(blocker, human);
        Assert.Contains(blocked, human);

        var outsider = await VisibleAsync(ds, s.Outsider, isOwner: false);
        Assert.DoesNotContain(blocker, outsider);
        Assert.DoesNotContain(blocked, outsider);
    }
}
