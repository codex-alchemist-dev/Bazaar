using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Scheduling;

namespace Bazaar.Server.Tests.Agents.Scheduling;

/// <summary>The fail-closed tool allowlist check for the wake loop.</summary>
public sealed class WakeToolAllowlistTests
{
    private static readonly ModelToolDefinition _gcPost = new("gc.post", "Posts a message.", """{"type":"object"}""");
    private static readonly ModelToolDefinition _kanbanMove = new("kanban.transition", "Moves a task.", """{"type":"object"}""");

    [Fact]
    public void An_empty_allowlist_passes_against_any_catalogue()
    {
        WakeToolAllowlist.EnsureOffered([], [_gcPost]);
    }

    [Fact]
    public void Every_allowlisted_name_offered_by_the_catalogue_passes()
    {
        WakeToolAllowlist.EnsureOffered(["kanban.transition", "gc.post"], [_gcPost, _kanbanMove]);
    }

    [Fact]
    public void A_name_no_component_offers_is_refused_and_named()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WakeToolAllowlist.EnsureOffered(["gc.post", "gc.delete"], [_gcPost]));

        Assert.Contains("gc.delete", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("gc.post", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_missing_name_is_listed_once_in_sorted_order()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WakeToolAllowlist.EnsureOffered(["zeta.run", "alpha.run", "alpha.run"], []));

        Assert.Contains("alpha.run, zeta.run", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, ex.Message.Split("alpha.run").Length - 1);
    }

    [Fact]
    public void A_missing_ryusei_tool_gets_the_project_hint()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WakeToolAllowlist.EnsureOffered(["ryusei.pause"], []));

        Assert.Contains("Bazaar:Ryusei:ProjectId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_non_ryusei_tool_gets_no_ryusei_hint()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WakeToolAllowlist.EnsureOffered(["gc.delete"], []));

        Assert.DoesNotContain("ProjectId", ex.Message, StringComparison.Ordinal);
    }
}
