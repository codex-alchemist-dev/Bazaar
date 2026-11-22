using System.Text.Json;
using Bazaar.Contracts.Community;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Community.Tests;

public class DraftReviewCardMapperTests
{
    private static JsonElement ModelOf(params object?[] cards)
    {
        var elements = cards.Select(c => c is DraftReviewCardDto d
            ? JsonSerializer.SerializeToElement(d, CommunityContractsJsonContext.Default.DraftReviewCardDto)
            : JsonSerializer.SerializeToElement(c)).ToArray();
        return JsonSerializer.SerializeToElement(new { cards = elements });
    }

    [Fact]
    public void Round_trips_a_card_through_the_pane_model()
    {
        var card = Fakes.Card();
        var result = DraftReviewCardMapper.Parse(new PaneDescriptor(PaneKinds.CommunityDrafts, "Drafts", string.Empty, ModelOf(card)));
        var parsed = Assert.Single(result.Cards);
        Assert.Equal(card.DraftId, parsed.DraftId);
        Assert.Equal(card.Body, parsed.Body);
        Assert.Equal(card.Approval.AvailableDecisions, parsed.Approval.AvailableDecisions);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Bad_entries_are_skipped_and_counted()
    {
        var result = DraftReviewCardMapper.Parse(ModelOf(Fakes.Card(), new { draftId = "not-a-guid" }, 5, new { }));
        Assert.Single(result.Cards);
        Assert.Equal(3, result.Skipped);
    }

    [Fact]
    public void Wrong_shape_throws_format_exception()
    {
        Assert.Throws<FormatException>(() => DraftReviewCardMapper.Parse(JsonSerializer.SerializeToElement(new { other = 1 })));
        Assert.Throws<FormatException>(() => DraftReviewCardMapper.Parse(JsonSerializer.SerializeToElement(1)));
    }

    [Fact]
    public void List_model_selects_first_and_moves()
    {
        var signer = new Fakes.Signer();
        var sender = new Fakes.Sender();
        var list = new DraftReviewViewModel([Fakes.Card(), Fakes.Card(), Fakes.Card()], signer, sender, new Fakes.Clock(Fakes.Now));
        Assert.Same(list.Cards[0], list.Selected);
        list.SelectNext();
        Assert.Same(list.Cards[1], list.Selected);
        list.SelectPrevious();
        list.SelectPrevious();
        Assert.Same(list.Cards[0], list.Selected);
        Assert.False(list.IsEmpty);
        Assert.True(new DraftReviewViewModel([], signer, sender).IsEmpty);
    }
}
