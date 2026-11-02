using Bazaar.Server.Fun.Lucky;
using Bazaar.Server.Sanitization;

namespace Bazaar.Server.Tests.Fun;

public class LuckyControllerTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly SensitiveTerms _terms = new(["Dana Ruiz"], [], [], [], []);
    private static readonly LuckyCandidate[] _candidates =
    [
        new("agent-a", OnShift: true, OptedIn: false),
        new("agent-b", OnShift: false, OptedIn: true),
        new("agent-c", OnShift: false, OptedIn: false),
    ];

    private static LuckyController Controller(LuckySettings? settings = null) =>
        new(settings ?? new LuckySettings(), new TextSanitizer());

    private static LuckyRun StartRun(LuckyController controller, Func<int>? seeds = null)
    {
        var result = controller.Start("run-1", "stuck on the cache", true, _start, 0, _candidates, seeds ?? (() => 7));
        return result.Run!;
    }

    [Fact]
    public void Only_the_owner_may_start_a_run()
    {
        var result = Controller().Start("run-1", "prompt", false, _start, 0, _candidates, () => 1);

        Assert.False(result.Accepted);
    }

    [Fact]
    public void The_daily_run_cap_refuses_a_second_run()
    {
        var result = Controller().Start("run-2", "prompt", true, _start, 1, _candidates, () => 1);

        Assert.False(result.Accepted);
    }

    [Fact]
    public void On_shift_or_opted_in_invites_those_agents_and_opted_in_only_invites_fewer()
    {
        var broad = StartRun(Controller());
        var narrow = StartRun(Controller(new LuckySettings { Participation = LuckyParticipation.OptedInOnly }));

        Assert.Equal(["agent-a", "agent-b"], broad.Participants.Select(p => p.AgentId));
        Assert.Equal(["agent-b"], narrow.Participants.Select(p => p.AgentId));
    }

    [Fact]
    public void Recorded_seed_reproduces_the_same_persona()
    {
        var run = StartRun(Controller(), () => 12345);

        var persona = run.Participants[0].Persona;

        Assert.Equal(12345, run.Participants[0].Seed);
        Assert.Equal(persona, LuckyPersonas.Pick(12345));
    }

    [Fact]
    public void Ideas_past_the_per_agent_limit_are_refused()
    {
        var controller = Controller(new LuckySettings { MaxIdeas = 2 });
        var run = StartRun(controller);

        for (var i = 0; i < 2; i++)
        {
            run = controller.RecordIdea(run, "agent-a", $"idea {i}", _start.AddMinutes(1), _terms).Run!;
        }

        var refused = controller.RecordIdea(run, "agent-a", "one more", _start.AddMinutes(2), _terms);

        Assert.False(refused.Accepted);
        Assert.Equal(2, refused.Run!.Ideas.Length);
    }

    [Fact]
    public void Ideas_after_the_time_box_are_refused()
    {
        var controller = Controller(new LuckySettings { RunMinutes = 20 });
        var run = StartRun(controller);

        var late = controller.RecordIdea(run, "agent-a", "too late", _start.AddMinutes(20), _terms);

        Assert.False(late.Accepted);
    }

    [Fact]
    public void Non_participants_cannot_record_ideas()
    {
        var controller = Controller(new LuckySettings { Participation = LuckyParticipation.OptedInOnly });
        var run = StartRun(controller);

        var refused = controller.RecordIdea(run, "agent-a", "idea", _start.AddMinutes(1), _terms);

        Assert.False(refused.Accepted);
    }

    [Fact]
    public void A_secret_shaped_idea_is_quarantined_and_cannot_become_a_card()
    {
        var controller = Controller(new LuckySettings { CardsFromIdeas = LuckyCardsFromIdeas.OwnerMarked });
        var run = StartRun(controller);

        var recorded = controller.RecordIdea(run, "agent-b", "use password=hunter2 for the cache", _start.AddMinutes(1), _terms);
        var marked = controller.MarkForCard(recorded.Run!, 0, markedByOwner: true);

        Assert.Equal(LuckyIdeaStatus.Quarantined, recorded.Run!.Ideas[0].Status);
        Assert.Equal(string.Empty, recorded.Run.Ideas[0].Text);
        Assert.False(marked.Accepted);
    }

    [Fact]
    public void Names_are_removed_from_kept_ideas()
    {
        var controller = Controller();
        var run = StartRun(controller);

        var recorded = controller.RecordIdea(run, "agent-a", "ask Dana Ruiz about the cache", _start.AddMinutes(1), _terms);

        Assert.Equal(LuckyIdeaStatus.Kept, recorded.Run!.Ideas[0].Status);
        Assert.DoesNotContain("Dana Ruiz", recorded.Run.Ideas[0].Text);
    }

    [Fact]
    public void Only_the_owner_can_mark_an_idea_for_a_card()
    {
        var controller = Controller();
        var run = StartRun(controller);
        var recorded = controller.RecordIdea(run, "agent-a", "cache per branch", _start.AddMinutes(1), _terms).Run!;

        Assert.False(controller.MarkForCard(recorded, 0, markedByOwner: false).Accepted);
        Assert.True(controller.MarkForCard(recorded, 0, markedByOwner: true).Accepted);
    }

    [Fact]
    public void No_card_is_offered_when_the_setting_is_none()
    {
        var controller = Controller(new LuckySettings { CardsFromIdeas = LuckyCardsFromIdeas.None });
        var run = StartRun(controller);
        var recorded = controller.RecordIdea(run, "agent-a", "cache per branch", _start.AddMinutes(1), _terms).Run!;

        Assert.False(controller.MarkForCard(recorded, 0, markedByOwner: true).Accepted);
    }

    [Fact]
    public void Wake_list_is_the_participants_in_order()
    {
        var run = StartRun(Controller());

        Assert.Equal(["agent-a", "agent-b"], LuckyController.WakeList(run));
    }
}
