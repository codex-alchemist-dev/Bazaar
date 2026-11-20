using Bazaar.Server.Hive;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Hive;

/// <summary>The Postgres grant store and ledger against a real PostgreSQL, with the same expectations as the in-memory stores.</summary>
public sealed class PgHiveStoreTests(HiveDb db) : IClassFixture<HiveDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static HivePendingCopy Pending(Guid id, string slug, DateTimeOffset? releaseAt) => new(
        id, new LessonRef("alpha", "memory/procedural/" + slug + ".md", HiveKind.Procedure), "beta", slug, "Title " + slug, "Body\n", "T2", _t0, releaseAt);

    // The test class shares one database, and xUnit runs the facts of a class one after another, so each fact clears first.
    private async Task<(PgHiveGrantStore Grants, PgHiveLedger Ledger)> FreshAsync()
    {
        SkipWithoutDatabase();
        await db.ClearAsync();
        return (new PgHiveGrantStore(db.DataSource), new PgHiveLedger(db.DataSource));
    }

    [Fact]
    public async Task Empty_store_grants_nothing()
    {
        var (grants, _) = await FreshAsync();

        Assert.False(await grants.IsGrantedAsync("alpha", "beta", HiveKind.Procedure, Ct));
        Assert.Empty(await grants.ListAsync(Ct));
    }

    [Fact]
    public async Task Grant_covers_only_its_pair_and_kind()
    {
        var (grants, _) = await FreshAsync();
        await grants.AddAsync(new HiveGrant("alpha", "beta", HiveKind.Procedure, "owner"), Ct);

        Assert.True(await grants.IsGrantedAsync("alpha", "beta", HiveKind.Procedure, Ct));
        Assert.False(await grants.IsGrantedAsync("alpha", "beta", HiveKind.Failure, Ct));
        Assert.False(await grants.IsGrantedAsync("beta", "alpha", HiveKind.Procedure, Ct));
        Assert.False(await grants.IsGrantedAsync("alpha", "gamma", HiveKind.Procedure, Ct));
    }

    [Fact]
    public async Task Adding_an_existing_grant_changes_nothing_and_list_keeps_grant_order()
    {
        var (grants, _) = await FreshAsync();
        var first = new HiveGrant("alpha", "beta", HiveKind.Procedure, "owner");
        var second = new HiveGrant("alpha", "gamma", HiveKind.TechnicalFact, "owner");
        await grants.AddAsync(first, Ct);
        await grants.AddAsync(second, Ct);
        await grants.AddAsync(first with { GrantedBy = "someone-else" }, Ct);

        Assert.Equal([first, second], await grants.ListAsync(Ct));
    }

    [Fact]
    public async Task Remove_reports_whether_a_grant_matched()
    {
        var (grants, _) = await FreshAsync();
        await grants.AddAsync(new HiveGrant("alpha", "beta", HiveKind.Failure, "owner"), Ct);

        Assert.True(await grants.RemoveAsync("alpha", "beta", HiveKind.Failure, Ct));
        Assert.False(await grants.RemoveAsync("alpha", "beta", HiveKind.Failure, Ct));
        Assert.False(await grants.IsGrantedAsync("alpha", "beta", HiveKind.Failure, Ct));
    }

    [Fact]
    public async Task Blank_or_equal_project_names_are_refused_before_the_table_is_reached()
    {
        var (grants, _) = await FreshAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            grants.AddAsync(new HiveGrant(" ", "beta", HiveKind.Procedure, "owner"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            grants.AddAsync(new HiveGrant("beta", "beta", HiveKind.Procedure, "owner"), Ct));
    }

    [Fact]
    public async Task Held_copy_round_trips_with_and_without_a_release_time()
    {
        var (_, ledger) = await FreshAsync();
        var timed = Pending(Guid.NewGuid(), "one", _t0.AddDays(30));
        var ownerOnly = Pending(Guid.NewGuid(), "two", null);
        await ledger.AddPendingAsync(timed, Ct);
        await ledger.AddPendingAsync(ownerOnly, Ct);

        Assert.Equal([timed, ownerOnly], await ledger.ListPendingAsync(Ct));
    }

    [Fact]
    public async Task Removing_a_held_copy_reports_whether_it_was_held()
    {
        var (_, ledger) = await FreshAsync();
        var held = Pending(Guid.NewGuid(), "one", _t0);
        await ledger.AddPendingAsync(held, Ct);

        Assert.True(await ledger.RemovePendingAsync(held.Id, Ct));
        Assert.False(await ledger.RemovePendingAsync(held.Id, Ct));
        Assert.Empty(await ledger.ListPendingAsync(Ct));
    }

    [Fact]
    public async Task Recording_the_same_copy_again_replaces_the_record_and_moves_it_last()
    {
        var (_, ledger) = await FreshAsync();
        var lesson = new LessonRef("alpha", "memory/procedural/one.md", HiveKind.Procedure);
        var other = new LessonRef("alpha", "memory/procedural/two.md", HiveKind.Procedure);
        await ledger.RecordCopyAsync(new HiveCopyRecord("hive/procedure/one.md", lesson, "beta", Revoked: true), Ct);
        await ledger.RecordCopyAsync(new HiveCopyRecord("hive/procedure/two.md", other, "beta", Revoked: false), Ct);
        await ledger.RecordCopyAsync(new HiveCopyRecord("hive/procedure/one.md", lesson, "beta", Revoked: false), Ct);

        Assert.Equal(
            [
                new HiveCopyRecord("hive/procedure/two.md", other, "beta", false),
                new HiveCopyRecord("hive/procedure/one.md", lesson, "beta", false),
            ],
            await ledger.ListCopiesAsync(Ct));
    }

    [Fact]
    public async Task Revoking_marks_only_matching_records_and_returns_them_as_they_were()
    {
        var (_, ledger) = await FreshAsync();
        var lesson = new LessonRef("alpha", "memory/procedural/one.md", HiveKind.Procedure);
        var forGamma = new HiveCopyRecord("hive/procedure/one-gamma.md", lesson, "gamma", false);
        var forBeta = new HiveCopyRecord("hive/procedure/one.md", lesson, "beta", false);
        var failure = new HiveCopyRecord("hive/failure/f.md", new LessonRef("alpha", "memory/failure/f.md", HiveKind.Failure), "beta", false);
        await ledger.RecordCopyAsync(forBeta, Ct);
        await ledger.RecordCopyAsync(forGamma, Ct);
        await ledger.RecordCopyAsync(failure, Ct);

        var affected = await ledger.MarkRevokedAsync("alpha", "beta", HiveKind.Procedure, Ct);

        Assert.Equal([forBeta], affected);
        Assert.Equal(
            [forBeta with { Revoked = true }, forGamma, failure],
            await ledger.ListCopiesAsync(Ct));
        Assert.Empty(await ledger.MarkRevokedAsync("alpha", "beta", HiveKind.Procedure, Ct));
    }
}
