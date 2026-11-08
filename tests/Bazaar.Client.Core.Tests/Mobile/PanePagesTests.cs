using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Core.Tests.Mobile;

public class PanePagesTests
{
    private static readonly string[] Three = ["one", "two", "three"];

    [Fact]
    public void Folded_posture_gives_one_pane_per_page()
    {
        var variant = PostureLayout.Select(PostureKind.Folded, HalfOpenLayoutPolicy.UnfoldedIfWide, 360);

        var pages = PanePages.Paginate(variant, Three);

        Assert.Equal(1, PanePages.PanesPerPage(variant));
        Assert.Equal(3, pages.Count);
        Assert.All(pages, page => Assert.Single(page));
        Assert.Equal("two", pages[1][0]);
    }

    [Fact]
    public void Unfolded_posture_gives_two_panes_per_page_with_a_short_last_page()
    {
        var variant = PostureLayout.Select(PostureKind.Unfolded, HalfOpenLayoutPolicy.UnfoldedIfWide, 900);

        var pages = PanePages.Paginate(variant, Three);

        Assert.Equal(2, PanePages.PanesPerPage(variant));
        Assert.Equal(2, pages.Count);
        Assert.Equal(["one", "two"], pages[0]);
        Assert.Equal(["three"], pages[1]);
    }

    [Fact]
    public void Half_open_wide_enough_paginates_as_unfolded()
    {
        var variant = PostureLayout.Select(PostureKind.HalfOpen, HalfOpenLayoutPolicy.UnfoldedIfWide, PostureLayout.MinUnfoldedWidthDp);

        Assert.Equal(2, PanePages.PanesPerPage(variant));
    }

    [Fact]
    public void No_panes_gives_no_pages()
    {
        Assert.Empty(PanePages.Paginate(LayoutVariant.Folded, []));
        Assert.Empty(PanePages.Paginate(LayoutVariant.Unfolded, []));
    }
}
