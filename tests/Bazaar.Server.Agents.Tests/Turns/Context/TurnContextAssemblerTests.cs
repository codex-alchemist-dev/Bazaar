using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Turns.Context;
using static Bazaar.Server.Tests.Agents.Turns.Context.ContextTestData;

namespace Bazaar.Server.Tests.Agents.Turns.Context;

public class TurnContextAssemblerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sections_are_assembled_in_fixed_order_whatever_the_source_order()
    {
        // Items are given in reverse section order; the messages must still follow the enum order.
        var assembler = Assembler(
            ContextBudget.Defaults,
            Item(ContextSection.Inbox, "in-1", "inbox text"),
            Item(ContextSection.GcBackread, "gc-1", "backread text"),
            Item(ContextSection.Vault, "vault-1", "vault text"),
            Item(ContextSection.TaskState, "task-1", "task text"),
            Item(ContextSection.Memory, "mem-1", "memory text"),
            Item(ContextSection.System, "sys-1", "system text"));

        var plan = await assembler.BuildAsync(Request(), Ct);

        Assert.Equal(
            ["system text", "memory text", "task text", "vault text", "backread text", "inbox text"],
            plan.Messages.Select(m => m.Text));
        Assert.Equal(ModelRole.System, plan.Messages[0].Role);
        Assert.All(plan.Messages.Skip(1), m => Assert.Equal(ModelRole.User, m.Role));
    }

    [Fact]
    public async Task Items_from_several_sources_keep_registration_order_within_a_section()
    {
        var assembler = new TurnContextAssembler(
            [
                new FixedContextSource(Item(ContextSection.Memory, "mem-a", "first")),
                new FixedContextSource(Item(ContextSection.Memory, "mem-b", "second")),
            ],
            ContextBudget.Defaults);

        var plan = await assembler.BuildAsync(Request(), Ct);

        Assert.Single(plan.Messages);
        Assert.Equal("first\n\nsecond", plan.Messages[0].Text);
    }

    [Fact]
    public async Task A_budgeted_section_keeps_the_earliest_items_and_stops_at_the_first_that_does_not_fit()
    {
        // Memory budget is 10 tokens. m1 (6) fits. m2 (5) would make 11, so the section stops there, even though m3 (1) would fit.
        var budget = ContextBudget.Defaults with { MemoryTokens = 10 };
        var assembler = Assembler(
            budget,
            Item(ContextSection.Memory, "m1", Sized("M1", 6)),
            Item(ContextSection.Memory, "m2", Sized("M2", 5)),
            Item(ContextSection.Memory, "m3", Sized("M3", 1)));

        var plan = await assembler.BuildAsync(Request(), Ct);

        var message = Assert.Single(plan.Messages);
        Assert.Equal(Sized("M1", 6), message.Text);
    }

    [Fact]
    public async Task A_section_whose_first_item_is_too_big_is_empty()
    {
        var budget = ContextBudget.Defaults with { TaskTokens = 3 };
        var assembler = Assembler(budget, Item(ContextSection.TaskState, "t1", Sized("T1", 4)));

        var plan = await assembler.BuildAsync(Request(), Ct);

        Assert.Empty(plan.Messages);
    }

    [Fact]
    public async Task The_trigger_thread_is_kept_even_when_earlier_backread_items_would_push_it_out()
    {
        // Backread budget is 10. The old thread (8) comes first and would fill the budget, which would drop the
        // trigger (4). The trigger is reserved first, so the old thread is the one that does not fit.
        var budget = ContextBudget.Defaults with { BackreadTokens = 10 };
        var assembler = Assembler(
            budget,
            Item(ContextSection.GcBackread, "gc-old", Sized("OLD", 8)),
            Item(ContextSection.GcBackread, "gc-trigger", Sized("TRIGGER", 4)));

        var plan = await assembler.BuildAsync(Request(triggerGcId: "gc-trigger", triggerGcSeq: 9), Ct);

        var message = Assert.Single(plan.Messages);
        Assert.Equal(Sized("TRIGGER", 4), message.Text);
    }

    [Fact]
    public async Task The_trigger_thread_fails_the_turn_when_it_cannot_fit_instead_of_being_skipped()
    {
        var budget = ContextBudget.Defaults with { BackreadTokens = 10 };
        var assembler = Assembler(budget, Item(ContextSection.GcBackread, "gc-trigger", Sized("TRIGGER", 12)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => assembler.BuildAsync(Request(triggerGcId: "gc-trigger"), Ct));

        Assert.Contains("never dropped", ex.Message);
        Assert.Contains("gc-trigger", ex.Message);
    }

    [Fact]
    public async Task The_trigger_thread_fails_the_turn_when_the_backread_does_not_contain_it()
    {
        var assembler = Assembler(
            ContextBudget.Defaults,
            Item(ContextSection.GcBackread, "gc-other", "other thread"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => assembler.BuildAsync(Request(triggerGcId: "gc-missing"), Ct));

        Assert.Contains("not in the GC backread", ex.Message);
    }

    [Fact]
    public async Task Without_a_trigger_the_backread_is_trimmed_like_any_other_section()
    {
        var budget = ContextBudget.Defaults with { BackreadTokens = 10 };
        var assembler = Assembler(
            budget,
            Item(ContextSection.GcBackread, "gc-old", Sized("OLD", 8)),
            Item(ContextSection.GcBackread, "gc-other", Sized("OTHER", 4)));

        var plan = await assembler.BuildAsync(Request(), Ct);

        Assert.Equal(Sized("OLD", 8), Assert.Single(plan.Messages).Text);
    }

    [Fact]
    public async Task Inbox_is_verbatim_and_not_budgeted()
    {
        // Every budgeted section has a zero ceiling. The inbox still comes through unchanged.
        var budget = new ContextBudget(0, 0, 0, 0, 0);
        var inboxText = "  Verbatim inbox line, with spacing and punctuation!  " + new string('x', 400);
        var assembler = Assembler(
            budget,
            Item(ContextSection.Memory, "m1", "memory"),
            Item(ContextSection.Inbox, "in-1", inboxText));

        var plan = await assembler.BuildAsync(Request(), Ct);

        var message = Assert.Single(plan.Messages);
        Assert.Equal(ModelRole.User, message.Role);
        Assert.Equal(inboxText, message.Text);
    }

    [Fact]
    public async Task Empty_sections_produce_no_message()
    {
        var assembler = Assembler(ContextBudget.Defaults, Item(ContextSection.Vault, "v1", "vault only"));

        var plan = await assembler.BuildAsync(Request(), Ct);

        var message = Assert.Single(plan.Messages);
        Assert.Equal(ModelRole.User, message.Role);
        Assert.Equal("vault only", message.Text);
    }

    [Fact]
    public async Task No_items_produce_no_messages_but_still_a_manifest()
    {
        var assembler = new TurnContextAssembler([], ContextBudget.Defaults);

        var plan = await assembler.BuildAsync(Request(), Ct);

        Assert.Empty(plan.Messages);
        Assert.Contains("\"sections\":[", plan.ManifestJson);
    }

    [Fact]
    public async Task Identical_input_gives_identical_messages_and_manifest()
    {
        ContextSourceItem[] items =
        [
            Item(ContextSection.System, "sys-1", "system", "sv1"),
            Item(ContextSection.Memory, "mem-1", Sized("MEM", 3), "mv1"),
            Item(ContextSection.GcBackread, "gc-1", Sized("GC", 3), "gv7"),
            Item(ContextSection.Inbox, "in-1", "wake", "iv1"),
        ];

        var first = await Assembler(ContextBudget.Defaults, items).BuildAsync(Request("gc-1", 7), Ct);
        var second = await Assembler(ContextBudget.Defaults, items).BuildAsync(Request("gc-1", 7), Ct);

        Assert.Equal(first.Messages.Select(m => (m.Role, m.Text)), second.Messages.Select(m => (m.Role, m.Text)));
        Assert.Equal(first.ManifestJson, second.ManifestJson);
    }

    [Fact]
    public async Task Token_estimate_is_chars_over_four_rounded_up()
    {
        Assert.Equal(0, CharQuarterTokenEstimator.Instance.Estimate(""));
        Assert.Equal(1, CharQuarterTokenEstimator.Instance.Estimate("a"));
        Assert.Equal(1, CharQuarterTokenEstimator.Instance.Estimate("abcd"));
        Assert.Equal(2, CharQuarterTokenEstimator.Instance.Estimate("abcde"));

        // A 5-character item with a 1-token ceiling is excluded, so the estimate drives the trim.
        var budget = ContextBudget.Defaults with { SystemTokens = 1 };
        var plan = await Assembler(budget, Item(ContextSection.System, "s1", "abcde")).BuildAsync(Request(), Ct);
        Assert.Empty(plan.Messages);
    }
}
