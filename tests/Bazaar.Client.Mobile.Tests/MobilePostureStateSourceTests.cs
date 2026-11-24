using Bazaar.Client.Posture;

namespace Bazaar.Client.Mobile.Tests;

public class MobilePostureStateSourceTests
{
    private static readonly FoldSnapshot _halfOpen = new(true, FoldState.HalfOpened, HingeIsHorizontal: true, HingeIsSeparating: false);

    [Theory]
    [InlineData(PostureKind.Folded)]
    [InlineData(PostureKind.Unfolded)]
    public void Folded_and_unfolded_are_flat_so_the_window_width_decides(PostureKind kind)
    {
        Assert.Equal(PostureState.Flat, MobilePostureStateSource.Map(kind));
    }

    [Fact]
    public void Half_open_has_no_known_hinge_or_angle()
    {
        PostureState state = MobilePostureStateSource.Map(PostureKind.HalfOpen);

        Assert.Equal(DevicePosture.HalfOpen, state.Posture);
        Assert.Null(state.FoldAngleDegrees);
        Assert.True(state.Hinge.IsNone);
    }

    [Fact]
    public void Changes_from_the_android_source_are_relayed_as_posture_states()
    {
        var android = new FakePostureSource();
        using var source = new MobilePostureStateSource(android);
        var seen = new List<PostureState>();
        source.Changed += (_, s) => seen.Add(s);

        android.Push(_halfOpen);

        Assert.Equal(DevicePosture.HalfOpen, Assert.Single(seen).Posture);
        Assert.Equal(DevicePosture.HalfOpen, source.Current.Posture);
    }

    [Fact]
    public void A_disposed_adapter_stops_relaying()
    {
        var android = new FakePostureSource();
        var source = new MobilePostureStateSource(android);
        var count = 0;
        source.Changed += (_, _) => count++;

        source.Dispose();
        android.Push(_halfOpen);

        Assert.Equal(0, count);
    }
}
