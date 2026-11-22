using System.Reflection;
using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Policy;
using Npgsql;

namespace Bazaar.Server.TalentPool.Component.Tests;

public class ComponentTests
{
    [Fact]
    public void Manifest_names_the_talent_pool_and_its_migration_namespace()
    {
        Assert.Equal("talent-pool", TalentPoolComponent.ManifestDefinition.Id);
        Assert.Equal(TalentPoolComponent.MigrationProvider, TalentPoolComponent.ManifestDefinition.MigrationNamespace);
    }

    [Fact]
    public void Migration_is_embedded_and_holds_no_memory_column()
    {
        var asm = typeof(TalentPoolComponent).Assembly;
        var name = Assert.Single(asm.GetManifestResourceNames(), n => n.EndsWith("0001_talent_pool.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
        var sql = reader.ReadToEnd();
        Assert.DoesNotContain("memory", sql.Replace("No memory column exists by design", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tool_hash_ignores_order_and_changes_with_content()
    {
        Assert.Equal(PostgresToolGrantApproval.ToolsSha(["b", "a"]), PostgresToolGrantApproval.ToolsSha(["a", "b"]));
        Assert.NotEqual(PostgresToolGrantApproval.ToolsSha(["a"]), PostgresToolGrantApproval.ToolsSha(["a", "b"]));
    }

    [Theory]
    [InlineData("anthropic:claude-haiku-5-5", GwModel.StatusAvailable, true)]
    [InlineData("anthropic:claude-haiku-5-5", GwModel.StatusMissing, false)]
    [InlineData("claude-haiku-5-5", GwModel.StatusAvailable, false)]
    [InlineData("anthropic:unknown", null, false)]
    public async Task Whitelist_allows_only_available_provider_qualified_models(string model, string? status, bool expected)
    {
        var whitelist = new GatewayTalentModelWhitelist(new FakeDirectory(status));
        Assert.Equal(expected, await whitelist.IsAllowedAsync(model, TestContext.Current.CancellationToken));
    }

    private sealed class FakeDirectory(string? status) : IGatewayDirectory
    {
        public ValueTask<GwProvider?> GetProviderAsync(string providerId, CancellationToken cancellationToken) => ValueTask.FromResult<GwProvider?>(null);

        public ValueTask<GwModel?> GetModelAsync(ModelRef model, CancellationToken cancellationToken) =>
            ValueTask.FromResult<GwModel?>(status is null ? null : new GwModel(model.ProviderId, model.ModelId, "claude", default!, "{}", status, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "test"));

        public ValueTask<GwRoute?> GetRouteAsync(string routeId, CancellationToken cancellationToken) => ValueTask.FromResult<GwRoute?>(null);
    }
}

/// <summary>Round trips against a real Postgres. Set BAZAAR_TEST_PG to a connection string for an empty database to run them.</summary>
public sealed class PostgresStoreTests
{
    private static readonly string? _conn = Environment.GetEnvironmentVariable("BAZAAR_TEST_PG");

    private static async Task<NpgsqlDataSource> FreshAsync()
    {
        Assert.SkipUnless(_conn is not null, "BAZAAR_TEST_PG is not set.");
        var source = NpgsqlDataSource.Create(_conn!);
        var asm = typeof(TalentPoolComponent).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("0001_talent_pool.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
        var sql = "DROP TABLE IF EXISTS talent_template, talent_hire_state, talent_tool_grant_approval;\n" + await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        await using var cmd = source.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return source;
    }

    [Fact]
    public async Task Templates_round_trip_and_latest_wins()
    {
        await using var source = await FreshAsync();
        var ct = TestContext.Current.CancellationToken;
        var store = new PostgresTalentTemplateStore(source);
        var manifest = new SoulManifest("Code Reviewer", "Review kindly.", ["shell"], ["anthropic:claude-haiku-5-5"], ["soul.md"], "soul-1");
        var sha = new string('a', 64);
        await store.AddAsync(new TalentTemplate("code-reviewer", 1, manifest, sha, "agent-a", DateTimeOffset.UtcNow), ct);
        await store.AddAsync(new TalentTemplate("code-reviewer", 2, manifest with { Prompt = "v2" }, new string('b', 64), "agent-a", DateTimeOffset.UtcNow), ct);

        var one = await store.FindAsync("code-reviewer", 1, ct);
        var latest = await store.FindAsync("code-reviewer", null, ct);
        Assert.Equal("Review kindly.", one!.Manifest.Prompt);
        Assert.Equal(["shell"], one.Manifest.ToolAllowlist);
        Assert.Equal(2, latest!.Version);
        Assert.Single(await store.ListLatestAsync(ct));
        Assert.Null(await store.FindAsync("nope", null, ct));
        await Assert.ThrowsAsync<PostgresException>(() => store.AddAsync(one, ct));
    }

    [Fact]
    public async Task Hire_state_upserts_and_tool_approvals_are_exact()
    {
        await using var source = await FreshAsync();
        var ct = TestContext.Current.CancellationToken;
        var hires = new PostgresHireDecisionStore(source);
        var vote = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await hires.SaveAsync(new HireVoteState("code-reviewer@1", vote, HireStatus.Pending, null, now), ct);
        await hires.SaveAsync(new HireVoteState("code-reviewer@1", vote, HireStatus.Released, MembershipDecision.Approved, now), ct);
        var state = await hires.FindAsync("code-reviewer@1", ct);
        Assert.Equal(HireStatus.Released, state!.Status);
        Assert.Equal(MembershipDecision.Approved, state.Decision);
        Assert.Null(await hires.FindAsync("other@1", ct));

        var approvals = new PostgresToolGrantApproval(source, TimeProvider.System);
        Assert.False(await approvals.IsApprovedAsync("code-reviewer@1", ["shell"], ct));
        await approvals.RecordAsync("code-reviewer@1", ["shell"], "owner", ct);
        await approvals.RecordAsync("code-reviewer@1", ["shell"], "owner", ct);
        Assert.True(await approvals.IsApprovedAsync("code-reviewer@1", ["shell"], ct));
        Assert.False(await approvals.IsApprovedAsync("code-reviewer@1", ["shell", "browser"], ct));
        Assert.False(await approvals.IsApprovedAsync("code-reviewer@2", ["shell"], ct));
    }
}
