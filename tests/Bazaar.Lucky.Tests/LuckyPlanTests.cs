using Bazaar.Lucky;
using Bazaar.Sanitization;
using Bazaar.Server.Agents.Inbox;
using Bazaar.Server.Sanitization;

namespace Bazaar.Lucky.Tests;

public class LuckyPlanTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly SensitiveTerms _terms = new(["Dana Ruiz"], [], [], [], []);
    private static readonly LuckyCandidate[] _candidates =
    [
        new("agent-a", OnShift: true, OptedIn: false),
        new("agent-b", OnShift: false, OptedIn: true),
        new("agent-c", OnShift: true, OptedIn: true),
    ];

    private static LuckyController Controller(LuckySettings? settings = null) =>
        new(settings ?? new LuckySettings(), new TextSanitizer());

    private static LuckyRun StartRun(LuckyController controller) =>
        controller.Start("run-1", "stuck on the cache", true, _start, 0, _candidates, () => 7).Run!;

    [Fact]
    public void Wake_kind_is_the_brainstorm_inbox_kind_and_only_opted_in_agents_are_woken()
    {
        var controller = Controller();
        var plan = controller.Plan(StartRun(controller));

        Assert.Equal(InboxKind.Brainstorm, "brainstorm");
        Assert.Equal(["agent-b", "agent-c"], plan.Wakes.Select(w => w.AgentId));
        Assert.All(plan.Wakes, w => Assert.Equal(InboxKind.Brainstorm, w.Kind));
    }

    [Fact]
    public void Each_wake_has_a_dedupe_key_unique_per_run_and_agent()
    {
        var controller = Controller();
        var plan = controller.Plan(StartRun(controller));

        Assert.Equal(["lucky:run-1:agent-b", "lucky:run-1:agent-c"], plan.Wakes.Select(w => w.DedupeKey));
    }

    [Fact]
    public void No_wakes_when_nobody_opted_in()
    {
        var controller = Controller(new LuckySettings { Participation = LuckyParticipation.OnShiftOrOptedIn });
        var candidates = new[] { new LuckyCandidate("agent-a", OnShift: true, OptedIn: false) };
        var run = controller.Start("run-2", "prompt", true, _start, 0, candidates, () => 1).Run!;

        Assert.Empty(controller.Plan(run).Wakes);
    }

    [Fact]
    public void The_brainstorm_gc_is_ephemeral_and_keyed_brainstorm()
    {
        var plan = Controller().Plan(StartRun(Controller()));

        Assert.Equal("brainstorm", LuckyController.GcTypeKey);
        Assert.Equal("brainstorm", plan.GcTypeKey);
        Assert.True(plan.Ephemeral);
        Assert.Equal("stuck on the cache", plan.Prompt);
    }

    [Fact]
    public void Kept_ideas_become_idea_entries_with_agent_source_trust_and_quarantined_ideas_do_not()
    {
        var controller = Controller();
        var run = StartRun(controller);
        run = controller.RecordIdea(run, "agent-b", "cache per branch", _start.AddMinutes(1), _terms).Run!;
        run = controller.RecordIdea(run, "agent-c", "password=hunter2", _start.AddMinutes(2), _terms).Run!;

        var plan = controller.Plan(run);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal("idea", entry.Kind);
        Assert.Equal("agent", entry.SourceTrust);
        Assert.Equal("agent-b", entry.AgentId);
        Assert.Equal("cache per branch", entry.Text);
    }

    [Fact]
    public void Cards_come_only_from_owner_marked_kept_ideas()
    {
        var controller = Controller();
        var run = StartRun(controller);
        run = controller.RecordIdea(run, "agent-b", "cache per branch", _start.AddMinutes(1), _terms).Run!;
        run = controller.RecordIdea(run, "agent-c", "password=hunter2", _start.AddMinutes(2), _terms).Run!;
        run = controller.RecordIdea(run, "agent-c", "shard the index", _start.AddMinutes(3), _terms).Run!;
        run = controller.MarkForCard(run, 0, markedByOwner: true).Run!;
        run = controller.MarkForCard(run, 2, markedByOwner: true).Run!;

        var plan = controller.Plan(run);

        Assert.Equal(["cache per branch", "shard the index"], plan.Cards.Select(c => c.Title));
        Assert.Equal([0, 2], plan.Cards.Select(c => c.IdeaIndex));
        Assert.All(plan.Cards, c => Assert.Equal("backlog", c.Column));
    }

    [Fact]
    public void No_cards_are_planned_when_the_setting_is_none()
    {
        var controller = Controller(new LuckySettings { CardsFromIdeas = LuckyCardsFromIdeas.None });
        var run = StartRun(controller);
        run = controller.RecordIdea(run, "agent-b", "cache per branch", _start.AddMinutes(1), _terms).Run!;

        Assert.Empty(controller.Plan(run).Cards);
    }

    [Fact]
    public void Long_idea_titles_are_cut_to_the_card_title_limit()
    {
        var controller = Controller();
        var run = StartRun(controller);
        var longText = new string('x', LuckyController.CardTitleLimit + 50);
        run = controller.RecordIdea(run, "agent-b", longText, _start.AddMinutes(1), _terms).Run!;
        run = controller.MarkForCard(run, 0, markedByOwner: true).Run!;

        var card = Assert.Single(controller.Plan(run).Cards);

        Assert.Equal(LuckyController.CardTitleLimit, card.Title.Length);
    }
}
