using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Core.Tests.Mobile;

public class PostureLayoutTests
{
    [Theory]
    [InlineData(PostureKind.Folded, 360)]
    [InlineData(PostureKind.Unfolded, 900)]
    [InlineData(PostureKind.Unfolded, 360)]
    public void Folded_and_unfolded_postures_ignore_width(PostureKind posture, double width)
    {
        var expected = posture == PostureKind.Unfolded ? LayoutVariant.Unfolded : LayoutVariant.Folded;

        Assert.Equal(expected, PostureLayout.Select(posture, HalfOpenLayoutPolicy.Folded, width));
    }

    [Fact]
    public void Half_open_uses_the_unfolded_layout_only_when_wide_enough()
    {
        Assert.Equal(LayoutVariant.Unfolded, PostureLayout.Select(PostureKind.HalfOpen, HalfOpenLayoutPolicy.UnfoldedIfWide, PostureLayout.MinUnfoldedWidthDp));
        Assert.Equal(LayoutVariant.Folded, PostureLayout.Select(PostureKind.HalfOpen, HalfOpenLayoutPolicy.UnfoldedIfWide, PostureLayout.MinUnfoldedWidthDp - 1));
    }

    [Fact]
    public void Half_open_with_folded_policy_stays_folded_at_any_width()
    {
        Assert.Equal(LayoutVariant.Folded, PostureLayout.Select(PostureKind.HalfOpen, HalfOpenLayoutPolicy.Folded, 1200));
    }
}
