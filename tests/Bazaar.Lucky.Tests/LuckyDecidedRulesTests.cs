using Bazaar.Lucky;
using Bazaar.Sanitization;
using Bazaar.Server.Sanitization;

namespace Bazaar.Lucky.Tests;

/// <summary>
/// Pins the fixed Lucky rules from doc 26, section 6 that no setting may override: owner-only trigger, the time box,
/// the daily cap, per-agent idea limits, agent source trust, and no idea becoming a card without the owner's mark.
/// </summary>
public class LuckyDecidedRulesTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly SensitiveTerms _terms = new(["Dana Ruiz"], [], [], [], []);
    private static readonly LuckyCandidate[] _candidates =
    [
        new("agent-a", OnShift: true, OptedIn: false),
        new("agent-b", OnShift: false, OptedIn: true),
    ];

    private static LuckyController Controller(LuckySettings? settings = null) =>
        new(settings ?? new LuckySettings(), new TextSanitizer());

    private static LuckyRun StartRun(LuckyController controller, int runsToday = 0) =>
        controller.Start("run-1", "stuck on the cache", true, _start, runsToday, _candidates, () => 7).Run!;

    [Fact]
    public void Owner_only_trigger_holds_under_every_participation_setting()
    {
        foreach (var participation in Enum.GetValues<LuckyParticipation>())
        {
            var controller = Controller(new LuckySettings { Participation = participation });

            var result = controller.Start("run-1", "prompt", false, _start, 0, _candidates, () => 1);

            Assert.False(result.Accepted);
            Assert.Null(result.Run);
        }
    }

    [Fact]
    public void Daily_cap_allows_runs_up_to_the_limit_and_no_further()
    {
        var controller = Controller(new LuckySettings { MaxRunsPerDay = 2 });

        Assert.True(controller.Start("run-1", "prompt", true, _start, 1, _candidates, () => 1).Accepted);
        Assert.False(controller.Start("run-2", "prompt", true, _start, 2, _candidates, () => 1).Accepted);
    }

    [Fact]
    public void A_refused_request_returns_the_run_unchanged()
    {
        var controller = Controller();
        var run = StartRun(controller);

        var refused = controller.RecordIdea(run, "agent-c", "not invited", _start.AddMinutes(1), _terms);

        Assert.False(refused.Accepted);
        Assert.Equal(run, refused.Run);
    }

    [Fact]
    public void An_idea_one_second_before_the_time_box_ends_is_accepted_and_one_at_the_end_is_refused()
    {
        var controller = Controller(new LuckySettings { RunMinutes = 20 });
        var run = StartRun(controller);

        var justInTime = controller.RecordIdea(run, "agent-a", "in time", _start.AddMinutes(20).AddSeconds(-1), _terms);
        var atTheEnd = controller.RecordIdea(run, "agent-a", "at the end", _start.AddMinutes(20), _terms);

        Assert.True(justInTime.Accepted);
        Assert.False(atTheEnd.Accepted);
    }

    [Fact]
    public void Quarantined_ideas_count_toward_the_per_agent_limit()
    {
        var controller = Controller(new LuckySettings { MaxIdeas = 1 });
        var run = StartRun(controller);

        var quarantined = controller.RecordIdea(run, "agent-b", "password=hunter2", _start.AddMinutes(1), _terms).Run!;
        var next = controller.RecordIdea(quarantined, "agent-b", "a clean idea", _start.AddMinutes(2), _terms);

        Assert.Equal(LuckyIdeaStatus.Quarantined, quarantined.Ideas[0].Status);
        Assert.False(next.Accepted);
    }

    [Fact]
    public void Every_recorded_idea_carries_agent_source_trust()
    {
        var controller = Controller();
        var run = StartRun(controller);

        var recorded = controller.RecordIdea(run, "agent-a", "cache per branch", _start.AddMinutes(1), _terms).Run!;

        Assert.Equal("agent", LuckyIdea.SourceTrust);
        Assert.False(recorded.Ideas[0].OwnerMarked);
    }

    [Fact]
    public void Ideas_do_not_become_cards_until_the_owner_marks_them()
    {
        var controller = Controller(new LuckySettings { CardsFromIdeas = LuckyCardsFromIdeas.OwnerMarked });
        var run = StartRun(controller);
        var recorded = controller.RecordIdea(run, "agent-a", "cache per branch", _start.AddMinutes(1), _terms).Run!;

        Assert.All(recorded.Ideas, idea => Assert.False(idea.OwnerMarked));
    }

    [Fact]
    public void Marking_an_idea_out_of_range_is_refused()
    {
        var controller = Controller();
        var run = StartRun(controller);
        var recorded = controller.RecordIdea(run, "agent-a", "cache per branch", _start.AddMinutes(1), _terms).Run!;

        Assert.False(controller.MarkForCard(recorded, 1, markedByOwner: true).Accepted);
        Assert.False(controller.MarkForCard(recorded, -1, markedByOwner: true).Accepted);
    }

    [Fact]
    public void Marking_the_same_idea_twice_is_idempotent()
    {
        var controller = Controller();
        var run = StartRun(controller);
        var recorded = controller.RecordIdea(run, "agent-a", "cache per branch", _start.AddMinutes(1), _terms).Run!;

        var once = controller.MarkForCard(recorded, 0, markedByOwner: true).Run!;
        var twice = controller.MarkForCard(once, 0, markedByOwner: true);

        Assert.True(twice.Accepted);
        Assert.True(twice.Run!.Ideas[0].OwnerMarked);
    }

    [Fact]
    public void Settings_below_one_are_rejected_when_the_controller_is_built()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Controller(new LuckySettings { MaxRunsPerDay = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Controller(new LuckySettings { MaxIdeas = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Controller(new LuckySettings { RunMinutes = 0 }));
    }

    [Fact]
    public void A_run_with_no_eligible_candidates_starts_with_no_participants()
    {
        var controller = Controller(new LuckySettings { Participation = LuckyParticipation.OptedInOnly });
        var nobodyOptedIn = new[] { new LuckyCandidate("agent-a", OnShift: true, OptedIn: false) };

        var result = controller.Start("run-1", "prompt", true, _start, 0, nobodyOptedIn, () => 1);

        Assert.True(result.Accepted);
        Assert.Empty(result.Run!.Participants);
    }

    [Fact]
    public void Only_opted_in_participants_are_woken_at_start()
    {
        var controller = Controller();
        var run = StartRun(controller);

        var wake = LuckyController.WakeList(run);

        Assert.DoesNotContain("agent-a", wake);
        Assert.Equal(["agent-b"], wake);
    }
}
