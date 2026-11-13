using Bazaar.Client.Core.Tour;

namespace Bazaar.Client.Core.Tests.Tour;

public class TourSessionTests
{
    private static TourDefinition FirstRun() => TourDefinition.Create("first-run",
    [
        new TourStep("chat", "tour.chat.title", "tour.chat.body", "gc-rail"),
        new TourStep("panes", "tour.panes.title", "tour.panes.body", "pane-tabs"),
        new TourStep("palette", "tour.palette.title", "tour.palette.body"),
    ]);

    [Fact]
    public void Start_shows_the_first_step_and_walks_forward()
    {
        TourSession session = TourSession.Start(FirstRun());

        Assert.Equal("chat", session.Current!.Id);
        session.Next();
        Assert.Equal("panes", session.Current!.Id);
        session.Next();
        Assert.Equal("palette", session.Current!.Id);
    }

    [Fact]
    public void Next_on_last_step_completes_the_tour()
    {
        TourSession session = TourSession.Start(FirstRun());
        session.Next();
        session.Next();
        session.Next();

        Assert.Equal(TourStatus.Completed, session.Status);
        Assert.Null(session.Current);
        Assert.Equal(-1, session.Index);
    }

    [Fact]
    public void Back_stops_at_the_first_step()
    {
        TourSession session = TourSession.Start(FirstRun());
        session.Back();
        Assert.Equal(0, session.Index);
        session.Next();
        session.Back();
        Assert.Equal("chat", session.Current!.Id);
    }

    [Fact]
    public void Skip_is_allowed_from_any_step_and_ends_the_tour()
    {
        TourSession session = TourSession.Start(FirstRun());
        session.Next();
        session.Skip();

        Assert.Equal(TourStatus.Skipped, session.Status);
        Assert.Null(session.Current);
        session.Next();
        Assert.Equal(TourStatus.Skipped, session.Status);
    }

    [Fact]
    public void Restart_runs_a_finished_tour_again()
    {
        TourSession session = TourSession.Start(FirstRun());
        session.Skip();

        session.Restart();

        Assert.Equal(TourStatus.Active, session.Status);
        Assert.Equal("chat", session.Current!.Id);
    }

    [Fact]
    public void Progress_is_saved_and_resumed_on_the_same_step()
    {
        var store = new InMemoryTourStateStore();
        TourSession session = TourSession.Start(FirstRun());
        session.Next();
        store.Save(session.Progress);

        TourSession resumed = TourSession.Resume(FirstRun(), store.Load("first-run"));

        Assert.Equal("panes", resumed.Current!.Id);
        Assert.Equal(TourStatus.Active, resumed.Status);
    }

    [Fact]
    public void Resume_with_a_removed_step_goes_back_to_the_first_step()
    {
        var saved = new TourProgress("first-run", "gone", TourStatus.Active);

        TourSession resumed = TourSession.Resume(FirstRun(), saved);

        Assert.Equal("chat", resumed.Current!.Id);
    }

    [Fact]
    public void Resume_keeps_a_finished_tour_finished()
    {
        var saved = new TourProgress("first-run", null, TourStatus.Completed);

        TourSession resumed = TourSession.Resume(FirstRun(), saved);

        Assert.Equal(TourStatus.Completed, resumed.Status);
        Assert.Null(resumed.Current);
    }

    [Fact]
    public void Progress_for_another_tour_is_ignored()
    {
        var saved = new TourProgress("other", "panes", TourStatus.Active);

        TourSession resumed = TourSession.Resume(FirstRun(), saved);

        Assert.Equal(TourStatus.Active, resumed.Status);
        Assert.Equal("chat", resumed.Current!.Id);
    }

    [Fact]
    public void Definition_rejects_empty_and_duplicate_steps()
    {
        Assert.Throws<ArgumentException>(() => TourDefinition.Create("t", []));
        Assert.Throws<ArgumentException>(() => TourDefinition.Create("t",
        [
            new TourStep("a", "k.t", "k.b"),
            new TourStep("a", "k.t", "k.b"),
        ]));
    }
}
