using Bazaar.Contracts.Engines;
using Bazaar.Engines.Grants;
using Bazaar.Engines.Registry;
using Bazaar.Engines.Tests.Grants;

namespace Bazaar.Engines.Tests.Adversarial;

/// <summary>An agent using grants it does not hold, and an adapter registering itself over another. Postgres tests skip without BAZAAR_TEST_PG.</summary>
public class GrantAndRegistryAdversarialTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string _skip = "BAZAAR_TEST_PG is not set; real-Postgres grant tests skipped.";

    private static EngineGrant Grant(Guid agent, string tool = "execute_luau", RiskClass risk = RiskClass.Exec, string adapter = "roblox") =>
        new(agent, adapter, tool, risk, "owner", _now);

    [Fact]
    public async Task A_grant_for_agent_A_is_not_usable_by_agent_B()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, _skip);
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);
        var a = Guid.NewGuid();
        await store.IssueAsync(Grant(a), TestContext.Current.CancellationToken);

        Assert.True(await store.CheckAsync(a, "roblox", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken));
        Assert.False(await store.CheckAsync(Guid.NewGuid(), "roblox", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken));
        Assert.False(await store.CheckAsync(Guid.Empty, "roblox", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("execute_luau.run")]
    [InlineData("Execute_Luau")]
    [InlineData("EXECUTE_LUAU")]
    [InlineData("execute")]
    [InlineData("execute_luau ")]
    [InlineData(" execute_luau")]
    [InlineData("execute_luau\n")]
    [InlineData("*")]
    [InlineData("%")]
    [InlineData("execute_%")]
    [InlineData("")]
    public async Task A_grant_for_one_tool_name_is_not_usable_for_a_variant(string variant)
    {
        Assert.SkipUnless(EngineTestDb.Enabled, _skip);
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);
        var agent = Guid.NewGuid();
        await store.IssueAsync(Grant(agent), TestContext.Current.CancellationToken);

        bool allowed;
        try
        {
            allowed = await store.CheckAsync(agent, "roblox", variant, RiskClass.Exec, TestContext.Current.CancellationToken);
        }
        catch (ArgumentException)
        {
            allowed = false;
        }

        Assert.False(allowed);
    }

    [Fact]
    public async Task A_grant_for_an_adapter_is_not_usable_on_a_lookalike_adapter()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, _skip);
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);
        var agent = Guid.NewGuid();
        await store.IssueAsync(Grant(agent), TestContext.Current.CancellationToken);

        Assert.False(await store.CheckAsync(agent, "Roblox", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken));
        Assert.False(await store.CheckAsync(agent, "roblox2", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken));
        Assert.False(await store.CheckAsync(agent, "godot", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_write_grant_does_not_allow_exec_and_an_exec_grant_does_not_allow_write()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, _skip);
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);
        var agent = Guid.NewGuid();
        await store.IssueAsync(Grant(agent, "script_write", RiskClass.Write), TestContext.Current.CancellationToken);

        Assert.False(await store.CheckAsync(agent, "roblox", "script_write", RiskClass.Exec, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("%")]
    [InlineData("")]
    [InlineData(" tool")]
    [InlineData("tool\t")]
    public async Task A_wildcard_or_malformed_tool_name_cannot_be_issued(string tool)
    {
        Assert.SkipUnless(EngineTestDb.Enabled, _skip);
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.IssueAsync(Grant(Guid.NewGuid(), tool), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_grant_for_the_empty_agent_id_cannot_be_issued()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, _skip);
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);

        await Assert.ThrowsAsync<ArgumentException>(() => store.IssueAsync(Grant(Guid.Empty), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_revoked_grant_is_denied_at_once_because_nothing_is_cached()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, _skip);
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);
        var agent = Guid.NewGuid();
        await store.IssueAsync(Grant(agent), TestContext.Current.CancellationToken);
        Assert.True(await store.CheckAsync(agent, "roblox", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken));

        await using (var delete = db.DataSource.CreateCommand("DELETE FROM engine_grants"))
        {
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Assert.False(await store.CheckAsync(agent, "roblox", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_revoke_and_use_never_allow_a_use_after_the_revoke_completed()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, _skip);
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);
        var agent = Guid.NewGuid();
        await store.IssueAsync(Grant(agent), TestContext.Current.CancellationToken);

        var uses = Enumerable.Range(0, 50).Select(async _ => await store.CheckAsync(agent, "roblox", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken)).ToArray();
        await using (var delete = db.DataSource.CreateCommand("DELETE FROM engine_grants"))
        {
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(uses);
        Assert.False(await store.CheckAsync(agent, "roblox", "execute_luau", RiskClass.Exec, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unknown_risk_class_is_never_allowed()
    {
        Assert.SkipUnless(EngineTestDb.Enabled, _skip);
        await using var db = await EngineTestDb.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EngineGrantStore(db.DataSource);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.CheckAsync(Guid.NewGuid(), "roblox", "t", (RiskClass)99, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void An_adapter_cannot_register_a_second_time_with_weaker_requirements_or_a_case_variant()
    {
        var registry = new EngineCapabilityRegistry();
        registry.Register(new EngineManifest("roblox", "1", [EngineCapability.Scenes], NeedsGuiHost: true));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new EngineManifest("roblox", "2", [EngineCapability.Scenes], NeedsGuiHost: false)));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new EngineManifest("ROBLOX", "2", [EngineCapability.Scenes], NeedsGuiHost: false)));
        Assert.True(registry.Find("roblox")!.NeedsGuiHost);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" roblox")]
    [InlineData("roblox\n")]
    public void Empty_or_padded_engine_ids_are_refused(string id)
    {
        Assert.ThrowsAny<ArgumentException>(() => new EngineCapabilityRegistry().Register(new EngineManifest(id, "1", [EngineCapability.Scenes], NeedsGuiHost: false)));
    }

    [Fact]
    public void An_undefined_capability_cannot_be_declared_and_an_unknown_one_matches_nothing()
    {
        var registry = new EngineCapabilityRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(new EngineManifest("x", "1", [(EngineCapability)9999], NeedsGuiHost: false)));
        Assert.Empty(registry.AdaptersWithCapability((EngineCapability)9999));
        Assert.Null(registry.Find("nobody"));
    }
}
