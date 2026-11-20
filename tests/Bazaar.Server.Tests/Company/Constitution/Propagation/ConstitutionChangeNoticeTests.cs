using Bazaar.Server.Agents.Inbox;
using Bazaar.Server.Company.Constitution.Propagation;

namespace Bazaar.Server.Tests.Company.Constitution.Propagation;

public class ConstitutionChangeNoticeTests
{
    private const string _from = "1111111111111111111111111111111111111111";
    private const string _to = "2222222222222222222222222222222222222222";
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _agentA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid _agentB = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static Func<Guid> SequentialIds()
    {
        var next = 0;
        return () => Guid.Parse($"00000000-0000-0000-0000-0000000001{++next:00}");
    }

    [Fact]
    public void Builds_one_constitution_item_per_distinct_active_agent()
    {
        var change = new ConstitutionChanged(_from, _to, ["Values"]);

        var notice = ConstitutionChangeNotice.Build(change, [_agentA, _agentB, _agentA], _now, SequentialIds());

        Assert.Equal(2, notice.InboxItems.Count);
        Assert.All(notice.InboxItems, item =>
        {
            Assert.Equal(InboxKind.Constitution, item.Kind);
            Assert.Null(item.GcId);
            Assert.Null(item.GcSeq);
            Assert.Equal(_now, item.DueAt);
        });
        Assert.Equal([_agentA, _agentB], notice.InboxItems.Select(item => item.AgentId));
    }

    [Fact]
    public void No_active_agents_gives_no_items()
    {
        var change = new ConstitutionChanged(_from, _to, ["Values"]);

        var notice = ConstitutionChangeNotice.Build(change, [], _now, SequentialIds());

        Assert.Empty(notice.InboxItems);
    }

    [Fact]
    public void Constitution_items_never_wake_an_agent()
    {
        var change = new ConstitutionChanged(_from, _to, ["Values"]);
        var notice = ConstitutionChangeNotice.Build(change, [_agentA], _now, SequentialIds());

        var plan = InboxSelector.Plan(notice.InboxItems, _now.AddMinutes(1));

        Assert.Null(plan.Primary);
        Assert.Empty(plan.Digest);
    }

    [Fact]
    public void A_constitution_item_does_not_hide_a_real_mention()
    {
        var change = new ConstitutionChanged(_from, _to, ["Values"]);
        var notice = ConstitutionChangeNotice.Build(change, [_agentA], _now, SequentialIds());
        var mention = new InboxCandidate(Guid.NewGuid(), _agentA, InboxKind.Mention, null, null, _now);

        var plan = InboxSelector.Plan([.. notice.InboxItems, mention], _now);

        Assert.Equal(mention, plan.Primary);
    }

    [Fact]
    public void Gc_notice_names_from_to_and_sections()
    {
        var change = new ConstitutionChanged(_from, _to, ["Values", "Escalation"]);

        var notice = ConstitutionChangeNotice.Build(change, [], _now, SequentialIds());

        Assert.Contains(_from, notice.GcText);
        Assert.Contains(_to, notice.GcText);
        Assert.Contains("Values, Escalation", notice.GcText);
    }

    [Fact]
    public void Gc_notice_for_the_first_activation_says_there_was_no_previous_commit()
    {
        var change = new ConstitutionChanged(null, _to, ["Values"]);

        var notice = ConstitutionChangeNotice.Build(change, [], _now, SequentialIds());

        Assert.Contains("first activation", notice.GcText);
        Assert.Contains(_to, notice.GcText);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        var change = new ConstitutionChanged(_from, _to, ["Values"]);

        Assert.Throws<ArgumentNullException>(() => ConstitutionChangeNotice.Build(null!, [], _now, SequentialIds()));
        Assert.Throws<ArgumentNullException>(() => ConstitutionChangeNotice.Build(change, null!, _now, SequentialIds()));
        Assert.Throws<ArgumentNullException>(() => ConstitutionChangeNotice.Build(change, [], _now, null!));
    }
}
