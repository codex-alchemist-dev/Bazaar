using Bazaar.Server.Fun.Vent;
using Bazaar.Server.Sanitization;

namespace Bazaar.Server.Tests.Fun;

public class VentDrafterTests
{
    private static readonly SensitiveTerms _terms = new(["Dana Ruiz"], ["Charles"], ["bazaar-repo"], [], []);
    private static readonly VentInput _input = new(
        "The CI failed on the push (GC seq 42). Dana Ruiz said the build was fine.",
        "Run the lint step before pushing; token sk-ant-abcdefghij12 leaked into the log.",
        "Whether the flaky test is in bazaar-repo or upstream.");

    private static VentDrafter Drafter() => new(new TextSanitizer());

    [Fact]
    public void Clean_source_writes_a_private_draft_with_names_and_secrets_removed()
    {
        var draft = Drafter().Draft("fail-1", "agent-a", _input, new VentTaint(false, false), _terms);

        Assert.Equal(VentStatus.Draft, draft.Status);
        Assert.DoesNotContain("Dana Ruiz", draft.WhatHappened);
        Assert.DoesNotContain("sk-ant-", draft.WhatWouldChange);
        Assert.DoesNotContain("bazaar-repo", draft.Unsure);
    }

    [Fact]
    public void Hard_tainted_draft_is_held_for_the_owner()
    {
        var draft = Drafter().Draft("fail-1", "agent-a", _input, new VentTaint(true, false), _terms);

        Assert.Equal(VentStatus.Held, draft.Status);
    }

    [Fact]
    public void Soft_taint_holds_only_when_the_source_session_was_tainted()
    {
        var drafter = Drafter();

        Assert.Equal(VentStatus.Draft, drafter.Draft("f", "a", _input, new VentTaint(false, false), _terms).Status);
        Assert.Equal(VentStatus.Held, drafter.Draft("f", "a", _input, new VentTaint(false, true), _terms).Status);
    }

    [Fact]
    public void Publishing_needs_owner_approval()
    {
        var drafter = Drafter();
        var draft = drafter.Draft("fail-1", "agent-a", _input, new VentTaint(false, false), _terms);

        var (published, refusal) = VentDrafter.Publish(draft, VentSettings.InternalGeneralGc, new VentSettings(), ownerApproved: false);

        Assert.Null(published);
        Assert.NotNull(refusal);
    }

    [Fact]
    public void Approved_internal_publish_moves_the_draft_to_published()
    {
        var drafter = Drafter();
        var draft = drafter.Draft("fail-1", "agent-a", _input, new VentTaint(false, false), _terms);

        var (published, refusal) = VentDrafter.Publish(draft, VentSettings.InternalGeneralGc, new VentSettings(), ownerApproved: true);

        Assert.Null(refusal);
        Assert.Equal(VentStatus.Published, published!.Status);
        Assert.Equal(VentSettings.InternalGeneralGc, published.PublishTarget);
    }

    [Fact]
    public void External_target_is_refused_unless_the_operator_configured_it()
    {
        var drafter = Drafter();
        var draft = drafter.Draft("fail-1", "agent-a", _input, new VentTaint(false, false), _terms);

        var (_, refused) = VentDrafter.Publish(draft, "public_forum", new VentSettings(), ownerApproved: true);
        var (ok, _) = VentDrafter.Publish(
            draft,
            "public_forum",
            new VentSettings { OperatorTargets = new HashSet<string> { "public_forum" } },
            ownerApproved: true);

        Assert.NotNull(refused);
        Assert.NotNull(ok);
    }

    [Fact]
    public void A_held_draft_is_released_only_by_owner_approval()
    {
        var drafter = Drafter();
        var held = drafter.Draft("fail-1", "agent-a", _input, new VentTaint(true, false), _terms);

        var (none, refusal) = VentDrafter.Publish(held, VentSettings.InternalGeneralGc, new VentSettings(), ownerApproved: false);
        var (released, _) = VentDrafter.Publish(held, VentSettings.InternalGeneralGc, new VentSettings(), ownerApproved: true);

        Assert.Null(none);
        Assert.NotNull(refusal);
        Assert.Equal(VentStatus.Published, released!.Status);
    }

    [Fact]
    public void Store_returns_the_first_draft_for_a_failure_so_restarts_are_idempotent()
    {
        var store = new InMemoryVentStore();
        var first = Drafter().Draft("fail-1", "agent-a", _input, new VentTaint(false, false), _terms);
        var second = Drafter().Draft("fail-1", "agent-a", _input with { Unsure = "different" }, new VentTaint(false, false), _terms);

        store.GetOrAdd(first);

        Assert.Equal(first, store.GetOrAdd(second));
    }
}
