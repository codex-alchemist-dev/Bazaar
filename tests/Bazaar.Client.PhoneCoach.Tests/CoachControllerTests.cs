using Bazaar.Client.Settings;

namespace Bazaar.Client.PhoneCoach.Tests;

public class CoachControllerTests
{
    private static ClientSettingsStore Store(string? path = null) => new(CoachController.Definitions, path);

    [Fact]
    public void Fresh_install_shows_the_first_prompt()
    {
        var coach = new CoachController(Store());

        Assert.True(coach.IsShowing);
        Assert.False(coach.Seen);
        Assert.Equal(CoachGesture.Swipe, coach.CurrentStep!.Gesture);
    }

    [Fact]
    public void Default_steps_are_swipe_then_hold()
    {
        Assert.Equal([CoachGesture.Swipe, CoachGesture.Hold], CoachStep.Default.Select(s => s.Gesture).ToArray());
    }

    [Fact]
    public void Next_walks_the_steps_and_the_last_one_finishes_and_marks_seen()
    {
        var settings = Store();
        var coach = new CoachController(settings);

        Assert.True(coach.Next());
        Assert.Equal(CoachGesture.Hold, coach.CurrentStep!.Gesture);
        Assert.True(coach.IsLastStep);
        Assert.True(coach.Next());

        Assert.False(coach.IsShowing);
        Assert.Null(coach.CurrentStep);
        Assert.Equal("true", settings.GetValue(CoachSettingKeys.Seen));
        Assert.False(coach.Next());
    }

    [Fact]
    public void Dismiss_from_the_first_step_persists_across_a_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"coach-{Guid.NewGuid():N}.json");
        try
        {
            new CoachController(Store(path)).Dismiss();

            var restarted = new CoachController(Store(path));

            Assert.True(restarted.Seen);
            Assert.False(restarted.IsShowing);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Replay_shows_the_coach_again_without_clearing_seen_and_does_not_flip_it_back()
    {
        var settings = Store();
        var coach = new CoachController(settings);
        coach.Dismiss();

        coach.Replay();
        Assert.True(coach.IsShowing);
        Assert.Equal(0, coach.StepIndex);
        Assert.True(coach.Seen);

        coach.Dismiss();
        Assert.False(coach.IsShowing);
        Assert.Equal("true", settings.GetValue(CoachSettingKeys.Seen));
    }

    [Fact]
    public void Changed_is_raised_for_each_step_and_for_dismiss()
    {
        var coach = new CoachController(Store());
        int raised = 0;
        coach.Changed += (_, _) => raised++;

        coach.Next();
        coach.Dismiss();

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Seen_default_is_false_and_the_definition_accepts_only_booleans()
    {
        var settings = Store();

        Assert.Equal("false", settings.GetValue(CoachSettingKeys.Seen));
        Assert.False(settings.TrySetValue(CoachSettingKeys.Seen, "maybe"));
    }

    [Fact]
    public void Custom_steps_replace_the_defaults()
    {
        var coach = new CoachController(Store(), [new(CoachGesture.Hold, "Eins", "Zwei")]);

        Assert.Equal("Eins", coach.CurrentStep!.Title);
        Assert.True(coach.IsLastStep);
    }
}
