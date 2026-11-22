using Bazaar.Server.Gc.Create;

namespace Bazaar.Server.Tests.Gc.Create;

/// <summary>Chaos round 3: a title cut to its limit must not be cut through a surrogate pair, which cannot be stored as UTF-8.</summary>
public sealed class GcCreateTitleDbTests(GcDb db) : IClassFixture<GcDb>
{
    [Fact]
    public async Task A_title_cut_at_the_limit_keeps_a_surrogate_pair_whole()
    {
        GcTestSkip.WithoutDatabase();
        var gcId = Guid.CreateVersion7();
        var creator = Guid.CreateVersion7();
        var title = new string('a', GcCreateService.MaxTitleLength - 1) + "😀😀";

        var created = await new GcCreateService(db.DataSource)
            .EnsureAsync(new GcCreateRequest(gcId, title, creator, [creator]), TestContext.Current.CancellationToken);

        Assert.True(created);
        var stored = await db.ScalarAsync<string>("SELECT title FROM gc WHERE gc_id = $1", gcId);
        Assert.Equal(new string('a', GcCreateService.MaxTitleLength - 1), stored);
    }
}
