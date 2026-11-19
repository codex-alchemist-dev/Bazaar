using Bazaar.Server.Agents.Scheduling;
using Bazaar.Server.Tests.Security.Approvals.Db;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Agents.Scheduling;

/// <summary>Per-agent tool grants against a real PostgreSQL: an agent is granted only what was granted to it.</summary>
public sealed class PostgresAgentToolGrantsTests
{
    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid _a = Guid.Parse("0196a6f0-0000-7000-8000-000000000a01");
    private static readonly Guid _b = Guid.Parse("0196a6f0-0000-7000-8000-000000000b01");

    [Fact]
    public async Task An_agent_with_no_grants_gets_no_tools()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var grants = new PostgresAgentToolGrants(db.DataSource);

        Assert.Empty(await grants.GrantedToolsAsync(_a, Ct));
    }

    [Fact]
    public async Task Grants_are_per_agent_ordered_and_revocable_without_erasing_history()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var grants = new PostgresAgentToolGrants(db.DataSource);

        await grants.GrantAsync(_a, "kanban.transition", "owner", Ct);
        await grants.GrantAsync(_a, "gc.post", "owner", Ct);
        await grants.GrantAsync(_b, "ryusei.list", "owner", Ct);

        Assert.Equal(["gc.post", "kanban.transition"], await grants.GrantedToolsAsync(_a, Ct));
        Assert.Equal(["ryusei.list"], await grants.GrantedToolsAsync(_b, Ct));

        await grants.RevokeAsync(_a, "gc.post", Ct);
        Assert.Equal(["kanban.transition"], await grants.GrantedToolsAsync(_a, Ct));

        await grants.GrantAsync(_a, "gc.post", "owner", Ct);
        Assert.Equal(["gc.post", "kanban.transition"], await grants.GrantedToolsAsync(_a, Ct));
    }
}
