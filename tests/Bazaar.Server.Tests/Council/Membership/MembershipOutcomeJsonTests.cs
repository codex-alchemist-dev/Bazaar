using System.Text.Json;
using Bazaar.Server.Council;
using Bazaar.Server.Council.Membership;

namespace Bazaar.Server.Tests.Council.Membership;

public class MembershipOutcomeJsonTests
{
    private static MembershipOutcome Sample() => new(
        Guid.Parse("6f1c2b7e-3d4a-4e8f-9a1b-2c3d4e5f6a7b"),
        "agent-candidate-7",
        MembershipMotionKind.ConstitutionChange,
        MembershipDecision.OwnerApproved,
        ApprovalChoices.Yes,
        "council.decision:6f1c2b7e-3d4a-4e8f-9a1b-2c3d4e5f6a7b",
        new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero),
        VaultDecisionNo: 12);

    [Fact]
    public void Schema_version_is_1()
    {
        Assert.Equal(1, MembershipOutcome.CurrentSchemaVersion);
        Assert.Equal(1, Sample().SchemaVersion);
    }

    [Fact]
    public void Round_trip_through_source_generated_context_preserves_the_outcome()
    {
        var outcome = Sample();

        var json = MembershipOutcomeJson.Serialize(outcome);
        var back = MembershipOutcomeJson.Deserialize(json);

        Assert.Equal(outcome, back);
    }

    [Fact]
    public void Json_has_no_reasoning_field_and_uses_camel_case_and_string_enums()
    {
        var json = MembershipOutcomeJson.Serialize(Sample());

        var root = JsonElement.Parse(json);
        var names = root.EnumerateObject().Select(property => property.Name).ToList();

        Assert.DoesNotContain(names, name => name.Contains("reasoning", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("reasoning", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("schemaVersion", names);
        Assert.Equal("constitution_change", root.GetProperty("kind").GetString());
        Assert.Equal("owner_approved", root.GetProperty("decision").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Withdrawn_outcome_serialises_null_choice_and_round_trips()
    {
        var outcome = MembershipOutcomeMapper.MapWithdrawn(
            new MembershipMotionMetadata(Guid.NewGuid(), MembershipMotionKind.Retire, "agent-9", MotionClass.Ordinary, DateTimeOffset.UnixEpoch));

        var json = MembershipOutcomeJson.Serialize(outcome);

        Assert.Equal(outcome, MembershipOutcomeJson.Deserialize(json));
        Assert.Equal("withdrawn", JsonElement.Parse(json).GetProperty("decision").GetString());
    }
}
