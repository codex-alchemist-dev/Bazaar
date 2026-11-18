using System.Text.Json;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Settings.Tests;

/// <summary>The abstraction types on their own: typed reads, write evaluation and the contribution shape. No server.</summary>
public sealed class SettingKeyTests
{
    private static readonly SettingActor _operator = new("operator:test", SettingRole.Operator);
    private static readonly SettingActor _agent = new("agent:test", SettingRole.Agent);

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private sealed class FakeContribution(IReadOnlyList<SettingDefinition> settings) : ISettingContribution
    {
        public IReadOnlyList<SettingDefinition> Settings { get; } = settings;
    }

    [Fact]
    public void Default_is_serialised_in_wire_form()
    {
        var key = new SettingKey<TestMode>("test.enum", TestMode.Strict, SettingScope.Install, SettingRole.Operator, "An enum.");
        Assert.Equal("\"strict\"", key.DefaultValueJson);
        Assert.Equal(typeof(TestMode), key.ValueType);
    }

    [Fact]
    public void Read_returns_typed_value_and_rejects_wrong_json()
    {
        var key = new SettingKey<int>("test.count", 3, SettingScope.Install, SettingRole.Operator, "A count.");

        Assert.Equal(7, key.Read(Json("7")));
        Assert.Throws<InvalidSettingValueException>(() => key.Read(Json("\"seven\"")));
        Assert.Throws<InvalidSettingValueException>(() => key.Read(Json("null")));
    }

    [Fact]
    public void Enum_accepts_snake_case_names_and_refuses_integers()
    {
        var key = new SettingKey<TestMode>("test.enum", TestMode.Strict, SettingScope.Install, SettingRole.Operator, "An enum.");

        Assert.Equal(TestMode.Relaxed, key.Read(Json("\"relaxed\"")));
        Assert.NotNull(key.CheckValue(Json("1")));
    }

    [Fact]
    public void Validator_rejects_a_default_that_fails_it()
    {
        Assert.Throws<ArgumentException>(() => new SettingKey<int>(
            "test.floor", 0, SettingScope.Install, SettingRole.Operator, "Floor.", validate: v => v < 1 ? "too low" : null));
    }

    [Fact]
    public void Evaluate_holds_an_approval_tier_change_and_accepts_an_ordinary_one()
    {
        var key = new SettingKey<bool>(
            "test.gated", false, SettingScope.Install, SettingRole.Operator, "Gated.", ApprovalTier.Tier2, requiresApprovalFor: v => v);

        var held = key.Evaluate(_operator, Json("true"));
        Assert.Equal(SettingWriteOutcome.PendingApproval, held.Outcome);
        Assert.Equal(ApprovalTier.Tier2, held.ApprovalTier);

        var applied = key.Evaluate(_operator, Json("false"));
        Assert.Equal(SettingWriteOutcome.Accepted, applied.Outcome);
    }

    [Fact]
    public void Evaluate_rejects_an_actor_without_the_required_role()
    {
        var key = new SettingKey<int>("test.owner_only", 1, SettingScope.Install, SettingRole.Owner, "Owner only.");

        var result = key.Evaluate(_agent, Json("2"));

        Assert.Equal(SettingWriteOutcome.Rejected, result.Outcome);
        Assert.False(_agent.Has(SettingRole.Owner));
    }

    [Fact]
    public void Contribution_exposes_the_keys_it_registers()
    {
        var key = new SettingKey<bool>("test.contributed", true, SettingScope.Install, SettingRole.Operator, "Contributed.");

        ISettingContribution contribution = new FakeContribution([key]);

        Assert.Same(key, Assert.Single(contribution.Settings));
    }

    private enum TestMode
    {
        Strict,
        Relaxed,
    }
}
