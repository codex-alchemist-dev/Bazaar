namespace Bazaar.Client.Posture.Tests;

public class FlatPostureSourceTests
{
    [Fact]
    public void A_desktop_head_is_always_flat_with_no_hinge()
    {
        var source = new FlatPostureSource();

        Assert.Equal(DevicePosture.Flat, source.Current.Posture);
        Assert.True(source.Current.Hinge.IsNone);
        Assert.Null(source.Current.FoldAngleDegrees);
    }

    [Fact]
    public void It_accepts_subscribers_and_never_raises()
    {
        var source = new FlatPostureSource();
        var raised = false;
        EventHandler<PostureState> handler = (_, _) => raised = true;

        source.Changed += handler;
        source.Changed -= handler;

        Assert.False(raised);
    }

    [Theory]
    [InlineData(400, PostureLayoutClass.Folded)]
    [InlineData(599, PostureLayoutClass.Folded)]
    [InlineData(600, PostureLayoutClass.DualPane)]
    [InlineData(1400, PostureLayoutClass.DualPane)]
    public void The_window_width_alone_decides_the_layout_from_600_dp(double width, PostureLayoutClass expected)
    {
        LayoutSelection selection = LayoutSelector.Select(new FlatPostureSource().Current, width, LayoutVariants.All);

        Assert.Equal(expected, selection.Class);
    }
}
