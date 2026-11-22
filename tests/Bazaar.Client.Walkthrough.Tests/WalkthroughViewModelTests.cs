namespace Bazaar.Client.Walkthrough.Tests;

public class WalkthroughViewModelTests
{
    private static async Task Press(System.Windows.Input.ICommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        await Task.Delay(30, TestContext.Current.CancellationToken);
    }

    private static (WalkthroughViewModel Model, FakeStore Store, FakeFacts Facts, FakeActions Actions) Make(
        WalkthroughFacts? facts = null, WalkthroughProgress? saved = null)
    {
        var store = new FakeStore(saved);
        var source = new FakeFacts(facts ?? WalkthroughFacts.None);
        var actions = new FakeActions();
        return (new WalkthroughViewModel(store, source, actions), store, source, actions);
    }

    [Fact]
    public async Task A_new_person_sees_the_welcome_step_and_walks_to_the_end_with_Next()
    {
        var (model, store, _, _) = Make();
        await model.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(model.Visible);
        Assert.Equal(WalkthroughStep.Welcome, model.Step);
        Assert.False(model.BackCommand.CanExecute(null));

        await Press(model.NextCommand);
        Assert.Equal(WalkthroughStep.CreateCompany, model.Step);
        await Press(model.NextCommand);
        await Press(model.NextCommand);
        Assert.Equal(WalkthroughStep.FirstTask, model.Step);
        await Press(model.NextCommand);
        Assert.Equal(WalkthroughStep.Done, model.Step);
        Assert.False(model.NextCommand.CanExecute(null));
        Assert.True(store.Saved!.Finished);
    }

    [Fact]
    public async Task Back_returns_to_the_previous_step_and_clears_finished()
    {
        var (model, store, _, _) = Make();
        await model.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 4; i++) await Press(model.NextCommand);

        await Press(model.BackCommand);

        Assert.Equal(WalkthroughStep.FirstTask, model.Step);
        Assert.False(store.Saved!.Finished);
    }

    [Fact]
    public async Task Skip_hides_the_walkthrough_and_a_restart_keeps_it_hidden()
    {
        var (model, store, facts, actions) = Make();
        await model.StartAsync(TestContext.Current.CancellationToken);
        await Press(model.SkipCommand);

        Assert.False(model.Visible);
        Assert.True(store.Saved!.Dismissed);

        var again = new WalkthroughViewModel(store, facts, actions);
        await again.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(again.Visible);
    }

    [Fact]
    public async Task Replay_shows_it_again_from_the_start()
    {
        var (model, store, _, _) = Make(saved: new WalkthroughProgress(true, true, WalkthroughStep.Done, [WalkthroughStep.CreateCompany]));
        await model.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(model.Visible);

        await model.ReplayAsync(TestContext.Current.CancellationToken);

        Assert.True(model.Visible);
        Assert.Equal(WalkthroughStep.Welcome, model.Step);
        Assert.False(store.Saved!.Dismissed);
        Assert.False(store.Saved.Finished);
    }

    [Fact]
    public async Task Facts_mark_steps_done_and_name_the_agent()
    {
        var (model, _, facts, _) = Make();
        await model.StartAsync(TestContext.Current.CancellationToken);
        await Press(model.NextCommand);
        Assert.False(model.IsStepDone);

        facts.Facts = new WalkthroughFacts(true, "Claude", false);
        await Press(model.RefreshCommand);
        Assert.True(model.IsStepDone);

        await Press(model.NextCommand);
        Assert.Equal(WalkthroughStep.MeetAgent, model.Step);
        Assert.Contains("Claude", model.Body);
        Assert.True(model.IsStepDone);
    }

    [Fact]
    public async Task A_returning_person_with_a_company_skips_past_the_done_steps_after_the_welcome_was_seen()
    {
        var saved = new WalkthroughProgress(false, false, WalkthroughStep.CreateCompany, []);
        var (model, _, _, _) = Make(new WalkthroughFacts(true, "Claude", false), saved);

        await model.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WalkthroughStep.FirstTask, model.Step);
    }

    [Fact]
    public async Task The_action_opens_the_steps_real_screen_and_reports_a_failure_without_throwing()
    {
        var (model, _, _, actions) = Make();
        await model.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(model.ActionCommand.CanExecute(null)); // the welcome has no action
        await Press(model.NextCommand);

        await Press(model.ActionCommand);
        Assert.Equal([WalkthroughStep.CreateCompany], actions.Opened);
        Assert.Equal("Opened.", model.Status);

        actions.Result = false;
        await Press(model.ActionCommand);
        Assert.Contains("could not be opened", model.Status);
    }

    [Fact]
    public async Task An_unreachable_server_and_a_broken_store_do_not_stop_the_walkthrough()
    {
        var (model, store, facts, _) = Make();
        store.Throws = true;
        facts.Throws = true;

        await model.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(model.Visible);
        Assert.Equal(WalkthroughStep.Welcome, model.Step);
        Assert.Contains("Could not check", model.Status);

        await Press(model.NextCommand);
        Assert.Equal(WalkthroughStep.CreateCompany, model.Step);
    }

    [Fact]
    public void Every_step_has_text_and_the_count_matches()
    {
        foreach (var step in Enum.GetValues<WalkthroughStep>())
        {
            Assert.NotEmpty(WalkthroughText.Title(step));
            Assert.NotEmpty(WalkthroughText.Body(step, WalkthroughFacts.None));
        }

        Assert.Equal(5, WalkthroughText.StepCount);
    }
}
