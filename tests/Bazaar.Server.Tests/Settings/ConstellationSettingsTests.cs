using System.Text.Json;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Settings;

/// <summary>Constellation keys from doc 19: scope, defaults, bounds and value checks. Pure: no database.</summary>
public sealed class ConstellationSettingsTests
{
    private static readonly SettingActor _operator = new("operator:dom", SettingRole.Operator);
    private static readonly SettingActor _owner = new("owner:dom", SettingRole.Owner);
    private static readonly SettingActor _agentActor = new("agent:charles", SettingRole.Agent);

    private static SettingEvaluation Eval(SettingDefinition definition, string json, SettingActor? actor = null)
    {
        using var doc = JsonDocument.Parse(json);
        return definition.Evaluate(actor ?? _operator, doc.RootElement);
    }

    [Fact]
    public void Constellation_registry_holds_every_doc_19_key_with_its_scope_and_role()
    {
        string[] expected =
        [
            "constellation.node_cap", "constellation.ring_depth", "constellation.git_layout",
            "constellation.orbit_radius", "constellation.screenshot_sources",
        ];

        var names = SettingRegistry.Constellation.Definitions.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), names);

        Assert.All(SettingRegistry.Constellation.Definitions, d => Assert.Equal(ApprovalTier.None, d.ApprovalTier));
        Assert.Equal(SettingScope.Install, ConstellationSettings.NodeCap.Scope);
        Assert.Equal(SettingScope.Install, ConstellationSettings.RingDepth.Scope);
        Assert.Equal(SettingScope.Install, ConstellationSettings.ScreenshotSources.Scope);
        Assert.Equal(SettingScope.Project, ConstellationSettings.GitLayout.Scope);
        Assert.Equal(SettingScope.Project, ConstellationSettings.OrbitRadius.Scope);
        Assert.Equal(SettingRole.Operator, ConstellationSettings.NodeCap.WhoMayChange);
        Assert.Equal(SettingRole.Owner | SettingRole.Operator, ConstellationSettings.GitLayout.WhoMayChange);
    }

    [Fact]
    public void Constellation_defaults_match_doc_19()
    {
        Assert.Equal(10000, ConstellationSettings.NodeCap.Default);
        Assert.Equal(3, ConstellationSettings.RingDepth.Default);
        Assert.Equal(ConstellationGitLayout.Deterministic, ConstellationSettings.GitLayout.Default);
        Assert.Equal(ConstellationOrbitRadius.Age, ConstellationSettings.OrbitRadius.Default);
        Assert.Equal([ConstellationScreenshotSource.GcAttachments], ConstellationSettings.ScreenshotSources.Default);
    }

    [Fact]
    public void Every_constellation_default_passes_its_own_check()
    {
        foreach (var definition in SettingRegistry.Constellation.Definitions)
        {
            using var doc = JsonDocument.Parse(definition.DefaultValueJson);
            Assert.Null(definition.CheckValue(doc.RootElement));
        }
    }

    [Theory]
    [InlineData("999", SettingWriteOutcome.Rejected)]
    [InlineData("1000", SettingWriteOutcome.Accepted)]
    [InlineData("50000", SettingWriteOutcome.Accepted)]
    [InlineData("50001", SettingWriteOutcome.Rejected)]
    [InlineData("\"10000\"", SettingWriteOutcome.Rejected)]
    public void Node_cap_bounds_are_1000_to_50000(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval(ConstellationSettings.NodeCap, json).Outcome);
    }

    [Fact]
    public void Node_cap_out_of_range_gives_a_clear_reason()
    {
        var result = Eval(ConstellationSettings.NodeCap, "50001");
        Assert.Contains("from 1000 to 50000", result.Reason);
    }

    [Theory]
    [InlineData("0", SettingWriteOutcome.Rejected)]
    [InlineData("1", SettingWriteOutcome.Accepted)]
    [InlineData("6", SettingWriteOutcome.Accepted)]
    [InlineData("7", SettingWriteOutcome.Rejected)]
    public void Ring_depth_bounds_are_1_to_6(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval(ConstellationSettings.RingDepth, json).Outcome);
    }

    [Theory]
    [InlineData("\"deterministic\"", SettingWriteOutcome.Accepted)]
    [InlineData("\"force\"", SettingWriteOutcome.Accepted)]
    [InlineData("\"auto\"", SettingWriteOutcome.Rejected)]
    [InlineData("0", SettingWriteOutcome.Rejected)]
    public void Git_layout_accepts_only_its_two_names(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval(ConstellationSettings.GitLayout, json, _owner).Outcome);
    }

    [Theory]
    [InlineData("\"age\"", SettingWriteOutcome.Accepted)]
    [InlineData("\"equal\"", SettingWriteOutcome.Accepted)]
    [InlineData("\"size\"", SettingWriteOutcome.Rejected)]
    public void Orbit_radius_accepts_only_its_two_names(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval(ConstellationSettings.OrbitRadius, json, _owner).Outcome);
    }

    [Theory]
    [InlineData("[\"gc_attachments\"]", SettingWriteOutcome.Accepted)]
    [InlineData("[\"gc_attachments\", \"test_artifacts\"]", SettingWriteOutcome.Accepted)]
    [InlineData("[]", SettingWriteOutcome.Accepted)]
    [InlineData("[\"gc_attachments\", \"gc_attachments\"]", SettingWriteOutcome.Rejected)]
    [InlineData("[\"video\"]", SettingWriteOutcome.Rejected)]
    [InlineData("\"gc_attachments\"", SettingWriteOutcome.Rejected)]
    public void Screenshot_sources_is_a_list_without_duplicates(string json, SettingWriteOutcome expected)
    {
        Assert.Equal(expected, Eval(ConstellationSettings.ScreenshotSources, json).Outcome);
    }

    [Fact]
    public void Constellation_keys_never_need_approval()
    {
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(ConstellationSettings.NodeCap, "50000").Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(ConstellationSettings.GitLayout, "\"force\"", _owner).Outcome);
    }

    [Fact]
    public void Agent_actor_cannot_change_constellation_keys()
    {
        var result = Eval(ConstellationSettings.OrbitRadius, "\"equal\"", _agentActor);
        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.Contains("may be changed only by", result.Reason);
    }

    [Fact]
    public void Owner_may_change_a_project_key_and_operator_may_too()
    {
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(ConstellationSettings.GitLayout, "\"force\"", _owner).Outcome);
        Assert.Equal(SettingWriteOutcome.Accepted, Eval(ConstellationSettings.GitLayout, "\"force\"", _operator).Outcome);
    }
}
