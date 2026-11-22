namespace Bazaar.Server.Setup.FirstRun.Postgres.Tests;

/// <summary>The default agent's whitelist comes only from the Claude models the gateway lists. Needs PostgreSQL.</summary>
public sealed class PostgresDefaultAgentRoutesTests
{
    [Fact]
    public async Task Every_available_claude_model_of_an_enabled_provider_is_listed_behind_one_route()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        await FirstRunDatabase.AddModelAsync(db.DataSource, "anthropic-direct", "claude-haiku-5-5", ct);
        await FirstRunDatabase.AddModelAsync(db.DataSource, "freellmapi", "claude-sonnet-5-5", ct, family: "pooled");
        await FirstRunDatabase.AddModelAsync(db.DataSource, "freellmapi", "gemini-2.5-flash", ct, family: "gemini");
        await FirstRunDatabase.AddModelAsync(db.DataSource, "off", "claude-opus", ct, providerEnabled: false);
        await FirstRunDatabase.AddModelAsync(db.DataSource, "anthropic-direct", "claude-old", ct, status: "missing");

        var whitelist = await new PostgresDefaultAgentRoutes(db.DataSource).GetWhitelistAsync([], "balanced", ct);

        Assert.Equal(["claude-first-agent", "anthropic-direct:claude-haiku-5-5", "freellmapi:claude-sonnet-5-5"], whitelist);
        Assert.Equal("[\"anthropic-direct:claude-haiku-5-5\", \"freellmapi:claude-sonnet-5-5\"]", await FirstRunDatabase.ScalarAsync<string>(db.DataSource, "SELECT members::text FROM gw_route WHERE id = 'claude-first-agent'", ct));
    }

    [Fact]
    public async Task Requested_sources_narrow_the_list_and_an_unlisted_source_adds_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);
        await FirstRunDatabase.AddModelAsync(db.DataSource, "anthropic-direct", "claude-haiku-5-5", ct);
        await FirstRunDatabase.AddModelAsync(db.DataSource, "freellmapi", "claude-sonnet-5-5", ct);
        var routes = new PostgresDefaultAgentRoutes(db.DataSource);

        Assert.Equal(["claude-first-agent", "freellmapi:claude-sonnet-5-5"], await routes.GetWhitelistAsync(["freellmapi"], "cost", ct));
        Assert.Empty(await routes.GetWhitelistAsync(["not-a-source"], "cost", ct));
    }

    [Fact]
    public async Task No_claude_source_means_an_empty_whitelist_and_no_route()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);

        Assert.Empty(await new PostgresDefaultAgentRoutes(db.DataSource).GetWhitelistAsync([], "balanced", ct));
        Assert.Equal(0, await FirstRunDatabase.ScalarAsync<long>(db.DataSource, "SELECT count(*) FROM gw_route", ct));
    }

    [Fact]
    public async Task An_unknown_profile_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await FirstRunDatabase.CreateAsync(ct);

        await Assert.ThrowsAsync<ArgumentException>(() => new PostgresDefaultAgentRoutes(db.DataSource).GetWhitelistAsync([], "fastest", ct));
    }
}
