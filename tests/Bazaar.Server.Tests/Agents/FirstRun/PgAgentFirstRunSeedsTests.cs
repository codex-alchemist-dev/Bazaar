using Bazaar.Server.Agents.Data;
using Bazaar.Server.Tests.Gc;
using Npgsql;

namespace Bazaar.Server.Tests.Agents.FirstRun;

/// <summary><see cref="PgAgentFirstRunSeeds"/> against the real schema (the <c>agents</c> migration over <c>common/0010</c>).</summary>
public sealed class PgAgentFirstRunSeedsTests(GcDb db) : IClassFixture<GcDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PgAgentFirstRunSeeds Seeds => new(db.DataSource);

    /// <summary>A marker no other test in this class uses: the class shares one database.</summary>
    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<string> StateAsync(Guid agentId)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT state FROM agent WHERE agent_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
        return (string)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task StartTurnAsync(Guid agentId)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO turn (agent_id, session_id, role, wake_reason) VALUES ($1, $2, 'actor', 'manual')");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7() });
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Held_agent_is_not_moved_by_the_automatic_step_until_released()
    {
        GcTestSkip.WithoutDatabase();
        var seeds = Seeds;
        var id = await seeds.CreateHeldAsync("Held agent", Unique("seed"), ct: Ct);

        Assert.False(await seeds.TryStartAutomaticOnboardingAsync(id, Ct));
        Assert.Equal("created", await StateAsync(id));

        Assert.True(await seeds.ReleaseHoldAsync(id, Ct));
        Assert.True(await seeds.TryStartAutomaticOnboardingAsync(id, Ct));
        Assert.Equal("onboarding", await StateAsync(id));
    }

    [Fact]
    public async Task Marker_lookup_is_idempotent_and_a_second_create_is_refused()
    {
        GcTestSkip.WithoutDatabase();
        var seeds = Seeds;
        var marker = Unique("seed");
        var id = await seeds.CreateHeldAsync("Marked agent", marker, ct: Ct);

        Assert.Equal(id, await seeds.FindByMarkerAsync(marker, Ct));
        Assert.Equal(id, await seeds.FindByMarkerAsync(marker, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => seeds.CreateHeldAsync("Again", marker, ct: Ct));
    }

    [Fact]
    public async Task Remove_unstarted_refuses_an_agent_with_a_turn_and_deletes_one_without()
    {
        GcTestSkip.WithoutDatabase();
        var seeds = Seeds;
        var started = await seeds.CreateHeldAsync("Started agent", Unique("seed"), ct: Ct);
        await StartTurnAsync(started);
        Assert.False(await seeds.RemoveUnstartedAsync(started, Ct));
        Assert.Equal("created", await StateAsync(started));

        var marker = Unique("seed");
        var unstarted = await seeds.CreateHeldAsync("Unstarted agent", marker, ct: Ct);
        Assert.True(await seeds.RemoveUnstartedAsync(unstarted, Ct));
        Assert.Null(await seeds.FindByMarkerAsync(marker, Ct));
        Assert.False(await seeds.RemoveUnstartedAsync(unstarted, Ct));
    }
}
