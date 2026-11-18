using Bazaar.Server.Hive.Abstractions;
using Bazaar.Testing;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Hive.Tests;

/// <summary>
/// A cloned test database with the <c>hive</c> component migrations applied. This project cannot reference Bazaar.Server, so it
/// applies the embedded component SQL itself, in file-name order (the Vms precedent).
/// </summary>
public sealed class HiveDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();

        var assembly = typeof(PostgresHiveGrantStore).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".components.hive.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("No hive migrations are embedded in Bazaar.Server.Hive.");

        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Embedded migration {name} is missing.");
            using var reader = new StreamReader(stream);
            await using var cmd = DataSource.CreateCommand(await reader.ReadToEndAsync());
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}

/// <summary>The Postgres stores on a real PostgreSQL, plus the whole flow wired to them.</summary>
public sealed class PostgresHiveStoreTests(HiveDb db) : IClassFixture<HiveDb>
{
    private static readonly ProjectId _a = new("pg-project-a");
    private static readonly ProjectId _b = new("pg-project-b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireDb() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Grants_add_once_list_and_remove()
    {
        RequireDb();
        var store = new PostgresHiveGrantStore(db.DataSource);
        var grant = new HiveGrant(_a, new ProjectId("pg-grants-target"), HiveKinds.Failure, "owner", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(await store.AddAsync(grant, Ct));
        Assert.False(await store.AddAsync(grant, Ct));
        Assert.True(await store.ExistsAsync(grant.FromProject, grant.ToProject, grant.Kind, Ct));
        Assert.False(await store.ExistsAsync(grant.FromProject, grant.ToProject, HiveKinds.Procedure, Ct));
        Assert.Contains(await store.ListAsync(Ct), g => g.ToProject.Equals(grant.ToProject) && g.GrantedBy == "owner");

        Assert.True(await store.RemoveAsync(grant.FromProject, grant.ToProject, grant.Kind, Ct));
        Assert.False(await store.RemoveAsync(grant.FromProject, grant.ToProject, grant.Kind, Ct));
    }

    [Fact]
    public async Task The_table_refuses_a_self_grant_an_unknown_kind_and_an_empty_grantor()
    {
        RequireDb();
        await AssertRejectedAsync("INSERT INTO hive_grants VALUES ('x', 'x', 'procedure', 'owner', now())");
        await AssertRejectedAsync("INSERT INTO hive_grants VALUES ('x', 'y', 'preference', 'owner', now())");
        await AssertRejectedAsync("INSERT INTO hive_grants VALUES ('x', 'y', 'procedure', '', now())");
    }

    [Fact]
    public async Task Copies_round_trip_and_move_through_their_states()
    {
        RequireDb();
        var store = new PostgresHiveCopyStore(db.DataSource);
        var t0 = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var copy = NewCopy("pg-copy-1", t0, t0.AddDays(30));

        Assert.True(await store.TryInsertAsync(copy, Ct));
        Assert.False(await store.TryInsertAsync(copy with { CopyId = Guid.CreateVersion7(), Path = "hive/procedure/other.md" }, Ct));
        Assert.Equal(copy, await store.GetAsync(copy.CopyId, Ct));
        Assert.Equal(copy, await store.FindAsync(copy.FromProject, copy.OriginMemory, copy.ToProject, Ct));

        Assert.DoesNotContain(await store.ListDueAsync(t0.AddDays(29), Ct), c => c.CopyId == copy.CopyId);
        Assert.Contains(await store.ListDueAsync(t0.AddDays(30), Ct), c => c.CopyId == copy.CopyId);

        Assert.True(await store.MarkActiveAsync(copy.CopyId, t0.AddDays(30), Ct));
        Assert.False(await store.MarkActiveAsync(copy.CopyId, t0.AddDays(31), Ct));
        Assert.Contains(await store.ListLiveAsync(copy.FromProject, copy.ToProject, copy.Kind, Ct), c => c.CopyId == copy.CopyId);

        Assert.True(await store.MarkDeprecatedAsync(copy.CopyId, t0.AddDays(40), Ct));
        Assert.False(await store.MarkDeprecatedAsync(copy.CopyId, t0.AddDays(41), Ct));
        var stored = (await store.GetAsync(copy.CopyId, Ct))!;
        Assert.Equal(HiveCopyState.Deprecated, stored.State);
        Assert.Equal(t0.AddDays(30), stored.ReleasedAt);
        Assert.Equal(t0.AddDays(40), stored.DeprecatedAt);
        Assert.DoesNotContain(await store.ListLiveAsync(copy.FromProject, copy.ToProject, copy.Kind, Ct), c => c.CopyId == copy.CopyId);
    }

    [Fact]
    public async Task A_copy_held_with_no_window_is_never_due()
    {
        RequireDb();
        var store = new PostgresHiveCopyStore(db.DataSource);
        var copy = NewCopy("pg-copy-nowindow", DateTimeOffset.UnixEpoch, null);
        Assert.True(await store.TryInsertAsync(copy, Ct));

        Assert.DoesNotContain(await store.ListDueAsync(DateTimeOffset.MaxValue, Ct), c => c.CopyId == copy.CopyId);
    }

    [Fact]
    public async Task The_table_refuses_a_copy_below_T2()
    {
        RequireDb();
        await AssertRejectedAsync(
            "INSERT INTO hive_copy VALUES (gen_random_uuid(), 'x', 'y', 'procedure', 'm', 'agent', 'T1', 't', 'b', 'hive/procedure/t1.md', 'in_review', now(), NULL, NULL, NULL)");
    }

    [Fact]
    public async Task The_whole_flow_runs_on_postgres_and_revoke_deprecates()
    {
        RequireDb();
        var from = new ProjectId("pg-flow-a");
        var to = new ProjectId("pg-flow-b");
        var clock = new FakeClock();
        var grants = new PostgresHiveGrantStore(db.DataSource);
        var copies = new PostgresHiveCopyStore(db.DataSource);
        var writer = new FakeVaultWriter();
        var audit = new FakeAudit();
        var settings = new FakeSettings(HiveOptions.Default);
        var grantService = new HiveGrantService(grants, copies, writer, audit, clock);
        var copyService = new HiveCopyService(grants, copies, new FakeSanitizer("acme"), writer, audit, settings, clock);
        var lesson = new LessonRef(from, "m-flow", HiveKinds.TechnicalFact, HiveSourceClass.Lesson, HiveTrust.T2, "agent", false, "Pin the SDK", "Pin the SDK in global.json.");

        Assert.Equal(HiveSubmitOutcome.NoGrant, (await copyService.SubmitAsync(lesson, to, Ct)).Outcome);
        await grantService.GrantAsync(from, to, HiveKinds.TechnicalFact, "owner", Ct);
        var held = await copyService.SubmitAsync(lesson, to, Ct);
        Assert.Equal(HiveSubmitOutcome.Held, held.Outcome);

        clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(1, await copyService.ReleaseDueAsync(Ct));
        Assert.Contains("status: active", writer.Files[held.Copy!.Path], StringComparison.Ordinal);

        Assert.True(await grantService.RevokeAsync(from, to, HiveKinds.TechnicalFact, "owner", Ct));
        Assert.Contains("status: deprecated", writer.Files[held.Copy.Path], StringComparison.Ordinal);
        Assert.Equal(HiveCopyState.Deprecated, (await copies.GetAsync(held.Copy.CopyId, Ct))!.State);
        Assert.False(await grants.ExistsAsync(from, to, HiveKinds.TechnicalFact, Ct));
    }

    private static HiveCopy NewCopy(string memory, DateTimeOffset heldAt, DateTimeOffset? reviewUntil) =>
        new(
            Guid.CreateVersion7(), _a, _b, HiveKinds.Procedure, memory, "agent", HiveTrust.T2, "A title", "A body.",
            $"hive/procedure/{memory}.md", HiveCopyState.InReview, heldAt, reviewUntil, null, null);

    private async Task AssertRejectedAsync(string sql)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
    }
}
