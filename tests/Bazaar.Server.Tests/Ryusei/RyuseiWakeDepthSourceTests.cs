using System.Text.Json.Nodes;
using Bazaar.Server.Kanban.Ryusei;
using Bazaar.Server.Tests.Kanban.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// <see cref="RyuseiWakeDepthSource"/> on real PostgreSQL (doc 38, Chain depth). Uses the Kanban fixture, which migrates every
/// namespace. Skipped when no test PostgreSQL is available (CI runs it). Written without a run: this container has no Docker,
/// so these tests are not verified here.
/// </summary>
public sealed class RyuseiWakeDepthSourceTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static readonly DateTimeOffset _instant = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private RyuseiWakeDepthSource Source => new(db.DataSource);

    private static NewRyuseiTrigger NewTimer(int chainDepth) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        RyuseiTriggerKind.Timer,
        [Guid.CreateVersion7()],
        "check the build",
        RyuseiSpecJson.Encode(new RyuseiTimerTriggerSpec(_instant)),
        TaskId: null,
        AttachGcId: null,
        RyuseiPingMode.None,
        NextFireAt: _instant,
        MaxFires: null,
        ExpiresAt: null,
        ChainDepth: chainDepth);

    /// <summary>Inserts a trigger and one woke fire of it, committed, and returns the fire id.</summary>
    private async Task<Guid> InsertCommittedFireAsync(int chainDepth)
    {
        var trigger = NewTimer(chainDepth);
        var fireId = Guid.CreateVersion7();
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var row = await new RyuseiTriggerRepository(db.DataSource).InsertAsync(tx, trigger, Ct);
        await RyuseiFireRepository.InsertAsync(tx, fireId, row.TriggerId, _instant, "woke", new JsonObject(), _instant, Ct);
        await tx.CommitAsync(Ct);
        return fireId;
    }

    [Fact]
    public async Task Fire_of_a_trigger_at_depth_two_reads_depth_two()
    {
        SkipWithoutDatabase();
        var fireId = await InsertCommittedFireAsync(chainDepth: 2);

        Assert.Equal(2, await Source.ChainDepthForFireAsync(fireId, Ct));
    }

    [Fact]
    public async Task Fire_of_a_root_trigger_reads_zero_not_null()
    {
        SkipWithoutDatabase();
        var fireId = await InsertCommittedFireAsync(chainDepth: 0);

        Assert.Equal(0, await Source.ChainDepthForFireAsync(fireId, Ct));
    }

    [Fact]
    public async Task Unknown_fire_reads_null()
    {
        SkipWithoutDatabase();

        Assert.Null(await Source.ChainDepthForFireAsync(Guid.CreateVersion7(), Ct));
    }
}
