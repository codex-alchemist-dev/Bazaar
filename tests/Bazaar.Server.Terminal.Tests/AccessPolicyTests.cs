using Bazaar.Server.Terminal.Abstractions;

namespace Bazaar.Server.Terminal.Tests;

public class AccessPolicyTests
{
    private static readonly TerminalInfo _terminal = new("t1", "agent-1", "vm-1", 80, 24, null, 0);

    private static (DefaultTerminalAccessPolicy Policy, TableGrants Grants) Make()
    {
        var grants = new TableGrants();
        return (new DefaultTerminalAccessPolicy(grants), grants);
    }

    [Fact]
    public void Owner_can_view_and_write_every_terminal()
    {
        var (p, _) = Make();
        Assert.True(p.CanView(Harness.Owner, _terminal));
        Assert.True(p.CanWrite(Harness.Owner, _terminal));
        Assert.True(p.CanView(Harness.Owner, _terminal with { AgentId = "agent-9" }));
    }

    [Fact]
    public void Agent_can_view_and_write_only_its_own_terminal()
    {
        var (p, _) = Make();
        Assert.True(p.CanView(Harness.Agent1, _terminal));
        Assert.True(p.CanWrite(Harness.Agent1, _terminal));
        Assert.False(p.CanView(Harness.Agent2, _terminal));
        Assert.False(p.CanWrite(Harness.Agent2, _terminal));
    }

    [Fact]
    public void An_agent_flagged_as_owner_gets_no_owner_rights()
    {
        var (p, _) = Make();
        var fake = new TerminalCaller("agent-2", IsOwner: true, IsAgent: true);
        Assert.False(p.CanView(fake, _terminal));
        Assert.False(p.CanWrite(fake, _terminal));
    }

    [Fact]
    public void Ungranted_human_is_denied_everything()
    {
        var (p, _) = Make();
        Assert.False(p.CanView(Harness.Human, _terminal));
        Assert.False(p.CanWrite(Harness.Human, _terminal));
    }

    [Fact]
    public void A_view_grant_allows_viewing_but_not_writing()
    {
        var (p, g) = Make();
        g.View.Add("human");
        Assert.True(p.CanView(Harness.Human, _terminal));
        Assert.False(p.CanWrite(Harness.Human, _terminal));
    }

    [Fact]
    public void A_write_grant_allows_writing_and_therefore_viewing()
    {
        var (p, g) = Make();
        g.Write.Add("human");
        Assert.True(p.CanWrite(Harness.Human, _terminal));
        Assert.True(p.CanView(Harness.Human, _terminal));
    }

    [Fact]
    public void Grants_for_one_member_do_not_apply_to_another()
    {
        var (p, g) = Make();
        g.Write.Add("someone-else");
        Assert.False(p.CanView(Harness.Human, _terminal));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_blank_member_id_is_denied_even_for_the_owner(string id)
    {
        var (p, _) = Make();
        Assert.False(p.CanView(new TerminalCaller(id, true, false), _terminal));
        Assert.False(p.CanWrite(new TerminalCaller(id, true, false), _terminal));
    }

    [Fact]
    public void A_terminal_without_an_agent_is_not_owned_by_any_agent()
    {
        var (p, _) = Make();
        Assert.False(p.CanView(Harness.Agent1, _terminal with { AgentId = null }));
        Assert.False(p.CanWrite(new TerminalCaller("", false, true), _terminal with { AgentId = "" }));
    }

    [Fact]
    public void Agent_ids_compare_exactly()
    {
        var (p, _) = Make();
        Assert.False(p.CanView(new TerminalCaller("Agent-1", false, true), _terminal));
        Assert.False(p.CanView(new TerminalCaller("agent-1 ", false, true), _terminal));
    }

    [Fact]
    public void No_grants_source_grants_nothing()
    {
        var g = new NoTerminalGrants();
        Assert.False(g.IsViewGranted("x", _terminal));
        Assert.False(g.IsWriteGranted("x", _terminal));
    }

    [Fact]
    public async Task List_and_subscribe_follow_the_access_matrix()
    {
        await using var h = new Harness();
        var a1 = await h.OpenAsync(Harness.Agent1, "agent-1");
        var a2 = await h.OpenAsync(Harness.Agent2, "agent-2");

        Assert.Equal(2, h.Broker.List(Harness.Owner).Count);
        Assert.Equal([a1.TerminalId], h.Broker.List(Harness.Agent1).Select(t => t.TerminalId));
        Assert.Equal([a2.TerminalId], h.Broker.List(Harness.Agent2).Select(t => t.TerminalId));
        Assert.Empty(h.Broker.List(Harness.Human));

        h.Grants.View.Add("human");
        Assert.Equal(2, h.Broker.List(Harness.Human).Count);

        var (denied, _) = await h.Broker.SubscribeAsync(Harness.Agent2, a1.TerminalId, 0, Harness.Ct);
        Assert.Equal(TerminalRefusal.Forbidden, denied.Refusal);
        var (ok, sub) = await h.Broker.SubscribeAsync(Harness.Owner, a1.TerminalId, 0, Harness.Ct);
        Assert.True(ok.Ok);
        await sub!.DisposeAsync();
    }

    [Fact]
    public async Task A_grant_source_that_throws_denies_instead_of_failing_open()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        h.Grants.Throw = true;
        Assert.Empty(h.Broker.List(Harness.Human));
        Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.SubscribeAsync(Harness.Human, info.TerminalId, 0, Harness.Ct)).Result.Refusal);
        Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.TakeControlAsync(Harness.Human, info.TerminalId, Harness.Ct)).Refusal);
    }

    [Fact]
    public async Task A_viewer_who_is_not_the_writer_cannot_write_or_resize()
    {
        await using var h = new Harness();
        var info = await h.OpenAsync();
        Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.WriteAsync(Harness.Owner, info.TerminalId, new byte[1], Harness.Ct)).Refusal);
        Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.ResizeAsync(Harness.Owner, info.TerminalId, 10, 10, Harness.Ct)).Refusal);
        Assert.Equal(TerminalRefusal.Forbidden, (await h.Broker.WriteAsync(Harness.Agent2, info.TerminalId, new byte[1], Harness.Ct)).Refusal);
    }
}
