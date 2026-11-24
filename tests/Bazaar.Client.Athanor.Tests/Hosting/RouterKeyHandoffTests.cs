using Bazaar.Client.Athanor.Hosting;

namespace Bazaar.Client.Athanor.Tests.Hosting;

public class RouterKeyHandoffTests
{
    [Fact]
    public void Ctrl_chords_are_never_claimed_even_when_the_router_would_claim_them()
    {
        int calls = 0;
        var handoff = new RouterKeyHandoff(_ =>
        {
            calls++;
            return true;
        });

        Assert.False(handoff.TryClaim(HostingFixtures.Key("p", ctrl: true)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Plain_key_the_router_claims_is_claimed()
    {
        EditorKeyEvent? seen = null;
        var handoff = new RouterKeyHandoff(key =>
        {
            seen = key;
            return key.Key == "Escape";
        });

        Assert.True(handoff.TryClaim(HostingFixtures.Key("Escape")));
        Assert.Equal("Escape", seen!.Key);
    }

    [Fact]
    public void Plain_key_the_router_does_not_claim_goes_to_the_editor()
    {
        var handoff = new RouterKeyHandoff(_ => false);

        Assert.False(handoff.TryClaim(HostingFixtures.Key("j")));
    }

    [Fact]
    public void A_throwing_router_counts_as_not_claimed()
    {
        var handoff = new RouterKeyHandoff(_ => throw new InvalidOperationException("router fault"));

        Assert.False(handoff.TryClaim(HostingFixtures.Key("Escape")));
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new RouterKeyHandoff(null!));

        var handoff = new RouterKeyHandoff(_ => true);
        Assert.Throws<ArgumentNullException>(() => handoff.TryClaim(null!));
    }
}
