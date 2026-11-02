using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Core.Tests.Mobile;

public class RadialMenuTests
{
    private static RadialMenu Menu() => new(["Reply", "Approvals"]);

    [Fact]
    public void Press_opens_the_menu_with_dictation_live_at_the_centre()
    {
        var menu = Menu();

        menu.Press();

        Assert.Equal(RadialPhase.Open, menu.Phase);
        Assert.Equal(RadialSlotKind.Centre, menu.Slot.Kind);
        Assert.True(menu.Dictating);
    }

    [Fact]
    public void Releasing_in_the_centre_places_the_transcript()
    {
        var menu = Menu();
        menu.Press();

        Assert.Equal(RadialOutcome.PlaceTranscript, menu.Release());
        Assert.Equal(RadialPhase.Idle, menu.Phase);
        Assert.False(menu.Dictating);
    }

    [Fact]
    public void Moving_into_a_sector_cancels_dictation_and_release_opens_it()
    {
        var menu = Menu();
        menu.Press();

        menu.MoveTo(RadialSlot.ForSector("Approvals"));

        Assert.False(menu.Dictating);
        Assert.Equal(RadialOutcome.OpenSector, menu.Release());
    }

    [Fact]
    public void Returning_to_the_centre_after_a_sector_does_not_restart_dictation()
    {
        var menu = Menu();
        menu.Press();
        menu.MoveTo(RadialSlot.ForSector("Reply"));

        menu.MoveTo(RadialSlot.Centre);

        Assert.False(menu.Dictating);
        Assert.Equal(RadialOutcome.Cancelled, menu.Release());
    }

    [Fact]
    public void Releasing_on_an_empty_area_cancels()
    {
        var menu = Menu();
        menu.Press();
        menu.MoveTo(RadialSlot.Empty);

        Assert.Equal(RadialOutcome.Cancelled, menu.Release());
    }

    [Fact]
    public void Cancel_closes_without_placing_or_opening()
    {
        var menu = Menu();
        menu.Press();

        menu.Cancel();

        Assert.Equal(RadialPhase.Idle, menu.Phase);
        Assert.Equal(RadialOutcome.Cancelled, menu.Release());
    }

    [Fact]
    public void A_second_press_while_open_is_ignored()
    {
        var menu = Menu();
        menu.Press();
        menu.MoveTo(RadialSlot.ForSector("Reply"));

        menu.Press();

        Assert.Equal(RadialSlotKind.Sector, menu.Slot.Kind);
        Assert.False(menu.Dictating);
    }

    [Fact]
    public void Moving_to_an_unknown_sector_is_rejected()
    {
        var menu = Menu();
        menu.Press();

        Assert.Throws<ArgumentException>(() => menu.MoveTo(RadialSlot.ForSector("Start screen")));
    }

    [Fact]
    public void Empty_or_duplicate_sector_lists_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new RadialMenu([]));
        Assert.Throws<ArgumentException>(() => new RadialMenu(["Reply", "Reply"]));
    }
}
