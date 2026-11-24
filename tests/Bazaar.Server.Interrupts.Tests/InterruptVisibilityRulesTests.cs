namespace Bazaar.Server.Interrupts.Tests;

public sealed class InterruptVisibilityRulesTests
{
    private static readonly Guid _agent = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _other = Guid.Parse("00000000-0000-7000-8000-0000000000b2");

    private static InterruptFrame Frame(Guid agent) =>
        new(Guid.NewGuid(), agent, null, InterruptSource.Owner, "secret", Guid.NewGuid(), InterruptFrameState.Active, DateTimeOffset.UnixEpoch, null, 1);

    [Fact]
    public void Company_owner_sees_the_reason()
    {
        var viewer = new InterruptViewer(Guid.NewGuid(), true);
        Assert.True(InterruptVisibilityRules.CanSeeReason(Frame(_agent), viewer));
        Assert.Equal("secret", InterruptVisibilityRules.ReasonFor(Frame(_agent), viewer));
    }

    [Fact]
    public void Agent_sees_its_own_reason_only()
    {
        var viewer = new InterruptViewer(_agent, false);
        Assert.Equal("secret", InterruptVisibilityRules.ReasonFor(Frame(_agent), viewer));
        Assert.Equal(InterruptVisibilityRules.RedactedReason, InterruptVisibilityRules.ReasonFor(Frame(_other), viewer));
    }

    [Fact]
    public void Other_viewer_is_redacted()
    {
        var viewer = new InterruptViewer(Guid.NewGuid(), false);
        Assert.Equal(InterruptVisibilityRules.RedactedReason, InterruptVisibilityRules.ReasonFor(Frame(_agent), viewer));
    }

    [Fact]
    public void Anonymous_is_redacted_even_for_an_empty_agent_id()
    {
        Assert.False(InterruptVisibilityRules.CanSeeReason(Frame(_agent), InterruptViewer.Anonymous));
        Assert.False(InterruptVisibilityRules.CanSeeReason(Frame(Guid.Empty), InterruptViewer.Anonymous));
    }
}
