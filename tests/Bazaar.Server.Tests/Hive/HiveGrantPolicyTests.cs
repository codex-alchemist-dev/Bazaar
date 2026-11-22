using Bazaar.Server.Hive;
using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Tests.Hive;

public class HiveGrantPolicyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LessonRef _lesson = new("a", "projects/a/memory/lessons/x.md", HiveKind.Procedure);

    [Fact]
    public async Task Empty_store_allows_nothing()
    {
        var policy = new HiveGrantPolicy(new InMemoryHiveGrantStore());
        Assert.False(await policy.MayCopyAsync(_lesson, "b", Ct));
    }

    [Fact]
    public async Task Grant_allows_exactly_its_pair_and_kind()
    {
        var store = new InMemoryHiveGrantStore();
        await store.AddAsync(new HiveGrant("a", "b", HiveKind.Procedure, "owner"), Ct);
        var policy = new HiveGrantPolicy(store);

        Assert.True(await policy.MayCopyAsync(_lesson, "b", Ct));
        Assert.False(await policy.MayCopyAsync(_lesson, "c", Ct));
        Assert.False(await policy.MayCopyAsync(_lesson with { Kind = HiveKind.Failure }, "b", Ct));
        Assert.False(await policy.MayCopyAsync(_lesson with { OriginProject = "z" }, "b", Ct));
    }

    [Fact]
    public async Task Default_allowed_kinds_exclude_technical_facts_and_settings_can_narrow()
    {
        var store = new InMemoryHiveGrantStore();
        await store.AddAsync(new HiveGrant("a", "b", HiveKind.TechnicalFact, "owner"), Ct);
        Assert.False(await new HiveGrantPolicy(store).MayCopyAsync(_lesson with { Kind = HiveKind.TechnicalFact }, "b", Ct));

        await store.AddAsync(new HiveGrant("a", "b", HiveKind.Procedure, "owner"), Ct);
        var narrowed = new HiveGrantPolicy(store, new HiveCopyOptions { AllowedKinds = new HashSet<HiveKind>() });
        Assert.False(await narrowed.MayCopyAsync(_lesson, "b", Ct));
    }

    [Fact]
    public async Task Removing_a_grant_stops_copies_and_duplicates_are_ignored()
    {
        var store = new InMemoryHiveGrantStore();
        var grant = new HiveGrant("a", "b", HiveKind.Procedure, "owner");
        await store.AddAsync(grant, Ct);
        await store.AddAsync(grant, Ct);
        Assert.Single(await store.ListAsync(Ct));
        Assert.True(await store.RemoveAsync("a", "b", HiveKind.Procedure, Ct));
        Assert.False(await store.RemoveAsync("a", "b", HiveKind.Procedure, Ct));
        Assert.False(await new HiveGrantPolicy(store).MayCopyAsync(_lesson, "b", Ct));
    }
}
