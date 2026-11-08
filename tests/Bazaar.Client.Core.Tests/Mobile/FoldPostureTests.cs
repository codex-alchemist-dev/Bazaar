using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Core.Tests.Mobile;

public class FoldPostureTests
{
    [Theory]
    [InlineData(FoldState.Flat, true, true)]
    [InlineData(FoldState.Flat, false, false)]
    [InlineData(FoldState.HalfOpened, true, false)]
    [InlineData(FoldState.HalfOpened, false, false)]
    public void Without_a_folding_feature_the_posture_is_folded(FoldState state, bool horizontal, bool separating)
    {
        Assert.Equal(PostureKind.Folded, FoldPosture.Map(false, state, horizontal, separating));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Flat_fold_is_unfolded(bool hingeIsHorizontal)
    {
        Assert.Equal(PostureKind.Unfolded, FoldPosture.Map(true, FoldState.Flat, hingeIsHorizontal, false));
    }

    [Fact]
    public void Half_opened_with_a_horizontal_hinge_is_half_open()
    {
        Assert.Equal(PostureKind.HalfOpen, FoldPosture.Map(true, FoldState.HalfOpened, true, false));
    }

    [Fact]
    public void Half_opened_with_a_vertical_hinge_is_half_open_because_no_hinge_posture_exists()
    {
        Assert.Equal(PostureKind.HalfOpen, FoldPosture.Map(true, FoldState.HalfOpened, false, false));
    }

    [Theory]
    [InlineData(FoldState.Flat, true)]
    [InlineData(FoldState.Flat, false)]
    [InlineData(FoldState.HalfOpened, true)]
    [InlineData(FoldState.HalfOpened, false)]
    public void A_separating_feature_is_folded_whatever_the_state(FoldState state, bool horizontal)
    {
        Assert.Equal(PostureKind.Folded, FoldPosture.Map(true, state, horizontal, true));
    }
}
