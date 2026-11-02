using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls;
using Bazaar.Server.Souls.Retirement;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Souls.Retirement;

/// <summary>Retirement must leave the Soul's version chain as it was (docs/design/16, "Retire": Soul data is kept).</summary>
public sealed class RetirementSoulDataTests(SoulDb db) : IClassFixture<SoulDb>
{
    private const string _prefs = "{\"tone\":\"dry\",\"verbosity\":2}";

    private static readonly string _charter = new('a', 64);

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Retiring_an_agent_leaves_the_soul_version_chain_unchanged()
    {
        SkipWithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var versions = new SoulVersioningService(db.DataSource);
        var soulId = Guid.NewGuid();
        await versions.AppendAsync(new SoulVersionAppend(soulId, 0, _charter, _prefs, "owner:dom", "create"), ct);
        await versions.AppendAsync(new SoulVersionAppend(soulId, 1, _charter, "{\"tone\":\"warm\"}", "owner:dom", "tune"), ct);
        var before = await versions.ListAsync(soulId, ct);

        var rig = new RetirementRig(DateTimeOffset.UtcNow);
        var report = await rig.Coordinator.RetireAsync(
            new RetirementRequest(new SoulRetirementSubject(soulId, Guid.NewGuid())), ct);

        Assert.True(report.IsComplete);
        var after = await versions.ListAsync(soulId, ct);
        Assert.Equal(2, after.Count);
        Assert.Equal(before, after);
        Assert.Equal(2, await versions.GetHeadAsync(soulId, ct));
    }
}
