namespace Bazaar.Server.Setup.FirstRun.Postgres.Tests;

/// <summary>The read-only lookup of the seeded general GC. Needs PostgreSQL.</summary>
public sealed class PostgresGeneralGcReaderTests
{
    [Fact]
    public async Task Finds_the_seeded_general_gc_and_nothing_before_or_after_it_is_archived()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        var owner = await FirstRunDatabase.AddOwnerAsync(db.DataSource, ct);
        var store = new PostgresFirstRunStore(db.DataSource);
        var reader = new PostgresGeneralGcReader(db.DataSource);

        Assert.Null(await reader.GetGeneralGcIdAsync(ct));

        var gc = await store.EnsureGeneralGcAsync("General", owner, ct);
        Assert.Equal(gc.GcId, await reader.GetGeneralGcIdAsync(ct));

        await store.ArchiveGcAsync(gc.GcId, ct);
        Assert.Null(await reader.GetGeneralGcIdAsync(ct));
    }
}
