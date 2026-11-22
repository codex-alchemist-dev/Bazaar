using System.Text.Json;
using Bazaar.Contracts.Community;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Community;

/// <summary>The cards a pane model carried, and how many entries were unreadable and left out.</summary>
public sealed record DraftReviewParseResult(IReadOnlyList<DraftReviewCardDto> Cards, int Skipped);

/// <summary>
/// Reads the community-drafts pane model: <c>{ "cards": [ DraftReviewCardDto, ... ] }</c>, each entry in the wire shape of
/// <see cref="DraftReviewCardDto"/>. An entry that does not parse is left out and counted, so one bad card does not hide the rest.
/// </summary>
public static class DraftReviewCardMapper
{
    /// <summary>Parses the descriptor's model.</summary>
    /// <exception cref="FormatException">The model is not an object with a <c>cards</c> array.</exception>
    public static DraftReviewParseResult Parse(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return Parse(descriptor.Model);
    }

    /// <summary>Parses a model element.</summary>
    /// <exception cref="FormatException">The model is not an object with a <c>cards</c> array.</exception>
    public static DraftReviewParseResult Parse(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object
            || !model.TryGetProperty("cards", out var cards)
            || cards.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("model field 'cards' must be an array");
        }

        var result = new List<DraftReviewCardDto>();
        var skipped = 0;
        foreach (var element in cards.EnumerateArray())
        {
            if (TryCard(element, out var card))
            {
                result.Add(card);
            }
            else
            {
                skipped++;
            }
        }

        return new DraftReviewParseResult(result, skipped);
    }

    private static bool TryCard(JsonElement element, out DraftReviewCardDto card)
    {
        card = null!;
        try
        {
            var parsed = element.Deserialize(CommunityContractsJsonContext.Default.DraftReviewCardDto);

            // The records are not nullable-checked on the wire, so a missing field arrives as null.
            if (parsed is null
                || parsed.Gates is null
                || parsed.Approval is null
                || parsed.Approval.AvailableDecisions is null
                || parsed.Approval.QuotedExcerpts is null
                || parsed.Body is null
                || parsed.BodySha256 is null)
            {
                return false;
            }

            card = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
