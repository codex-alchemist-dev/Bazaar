namespace Bazaar.Server.CodingReview.Tests;

public sealed class ReviewerModelChooserTests
{
    [Fact]
    public void Prefers_a_model_from_another_family_even_when_a_same_family_model_sorts_first()
    {
        var pool = ReviewFixtures.Pool(
            "alpha-1",
            "alpha",
            new ReviewModelOption("alpha-2", "alpha"),
            new ReviewModelOption("zeta-1", "zeta"));

        var choice = ReviewerModelChooser.Choose(pool);

        Assert.NotNull(choice);
        Assert.Equal("zeta-1", choice.ModelId);
        Assert.Equal("zeta", choice.Family);
        Assert.False(choice.SameFamily);
    }

    [Fact]
    public void Falls_back_to_the_same_family_and_marks_it_when_no_other_family_exists()
    {
        var pool = ReviewFixtures.Pool(
            "alpha-1",
            "alpha",
            new ReviewModelOption("alpha-1", "alpha"),
            new ReviewModelOption("alpha-2", "alpha"));

        var choice = ReviewerModelChooser.Choose(pool);

        Assert.NotNull(choice);
        Assert.True(choice.SameFamily);
        Assert.Equal("alpha-2", choice.ModelId);
    }

    [Fact]
    public void A_pool_holding_only_the_author_model_still_reviews_and_is_marked_same_family()
    {
        var pool = ReviewFixtures.Pool("alpha-1", "alpha", new ReviewModelOption("alpha-1", "alpha"));

        var choice = ReviewerModelChooser.Choose(pool);

        Assert.NotNull(choice);
        Assert.Equal("alpha-1", choice.ModelId);
        Assert.True(choice.SameFamily);
    }

    [Fact]
    public void An_empty_pool_gives_no_reviewer()
    {
        Assert.Null(ReviewerModelChooser.Choose(ReviewFixtures.Pool("alpha-1", "alpha")));
    }

    [Fact]
    public void Candidates_without_an_id_or_family_are_ignored()
    {
        var pool = ReviewFixtures.Pool(
            "alpha-1",
            "alpha",
            new ReviewModelOption("", "zeta"),
            new ReviewModelOption("zeta-1", " "));

        Assert.Null(ReviewerModelChooser.Choose(pool));
    }

    [Fact]
    public void The_choice_does_not_depend_on_the_order_of_the_pool()
    {
        var forward = ReviewFixtures.Pool(
            "alpha-1",
            "alpha",
            new ReviewModelOption("beta-2", "beta"),
            new ReviewModelOption("beta-1", "beta"),
            new ReviewModelOption("gamma-1", "gamma"));
        var reversed = ReviewFixtures.Pool(
            "alpha-1",
            "alpha",
            new ReviewModelOption("gamma-1", "gamma"),
            new ReviewModelOption("beta-1", "beta"),
            new ReviewModelOption("beta-2", "beta"));

        Assert.Equal(ReviewerModelChooser.Choose(forward), ReviewerModelChooser.Choose(reversed));
        Assert.Equal("beta-1", ReviewerModelChooser.Choose(forward)!.ModelId);
    }

    [Fact]
    public void Family_comparison_ignores_case()
    {
        var pool = ReviewFixtures.Pool("alpha-1", "Alpha", new ReviewModelOption("alpha-2", "ALPHA"));

        var choice = ReviewerModelChooser.Choose(pool);

        Assert.NotNull(choice);
        Assert.True(choice.SameFamily);
    }
}
