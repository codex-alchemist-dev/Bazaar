using Bazaar.Server.Council;
using Bazaar.Server.Council.Eligibility;

namespace Bazaar.Server.Tests.Council.Eligibility;

public class ExtensionRulesTests
{
    [Fact]
    public void Default_is_one_extension()
    {
        Assert.Equal(1, VoteExtensionSettings.Defaults.Max);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void Extensions_above_one_are_rejected(int max) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoteExtensionSettings(max));

    [Fact]
    public void Negative_extensions_are_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoteExtensionSettings(-1));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Zero_and_one_extension_are_accepted(int max) =>
        Assert.Equal(max, new VoteExtensionSettings(max).Max);

    [Fact]
    public void Quorum_met_reveals_with_or_without_extensions_left()
    {
        Assert.Equal(VoteEvent.DeadlineWithQuorum, ExtensionRules.DeadlineEvent(true, 0, VoteExtensionSettings.Defaults));
        Assert.Equal(VoteEvent.DeadlineWithQuorum, ExtensionRules.DeadlineEvent(true, 1, VoteExtensionSettings.Defaults));
    }

    [Fact]
    public void Missed_quorum_with_an_extension_left_is_extended()
    {
        Assert.Equal(VoteEvent.DeadlineExtended, ExtensionRules.DeadlineEvent(false, 0, VoteExtensionSettings.Defaults));
    }

    [Fact]
    public void Missed_quorum_after_the_one_extension_escalates()
    {
        Assert.Equal(VoteEvent.DeadlineWithoutQuorum, ExtensionRules.DeadlineEvent(false, 1, VoteExtensionSettings.Defaults));
    }

    [Fact]
    public void Zero_extensions_escalate_on_the_first_missed_quorum()
    {
        Assert.Equal(VoteEvent.DeadlineWithoutQuorum, ExtensionRules.DeadlineEvent(false, 0, new VoteExtensionSettings(0)));
    }

    [Fact]
    public void Missed_quorum_through_the_state_machine_extends_once_then_escalates()
    {
        var state = VoteState.Collecting;

        Assert.True(VoteStateMachine.TryNext(state, ExtensionRules.DeadlineEvent(false, 0, VoteExtensionSettings.Defaults), out state));
        Assert.Equal(VoteState.Collecting, state);

        Assert.True(VoteStateMachine.TryNext(state, ExtensionRules.DeadlineEvent(false, 1, VoteExtensionSettings.Defaults), out state));
        Assert.Equal(VoteState.Escalated, state);
    }

    [Fact]
    public void Quorum_at_the_extended_deadline_reveals_the_vote()
    {
        var state = VoteStateMachine.Next(VoteState.Collecting, ExtensionRules.DeadlineEvent(false, 0, VoteExtensionSettings.Defaults));

        Assert.True(VoteStateMachine.TryNext(state, ExtensionRules.DeadlineEvent(true, 1, VoteExtensionSettings.Defaults), out var next));
        Assert.Equal(VoteState.Revealed, next);
    }

    [Fact]
    public void Negative_extensions_used_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExtensionRules.DeadlineEvent(false, -1, VoteExtensionSettings.Defaults));
}
