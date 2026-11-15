using System.Globalization;
using System.Text.Json;
using Bazaar.Client.Core.Kanban;
using Bazaar.Client.Core.Ryusei;
using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>One card on the board. <see cref="IsBlocked"/> is the derived Blocked marking, not a column.</summary>
public sealed record KanbanCard(Guid Id, string Title, bool IsBlocked);

/// <summary>One column in the fixed board order.</summary>
public sealed record KanbanColumn(string Name, IReadOnlyList<KanbanCard> Cards);

/// <summary>
/// Kanban board pane (doc 12). Model: the server's board snapshot, the same shape as <c>KanbanBoardSnapshot</c>:
/// <c>{ "projectId", "cards": [ { "id", "title", "state", "ownerAgentId", "unfinishedBlockerIds", "blockerTitles" } ] }</c>.
/// Ids are Guid strings and states are <c>backlog</c>, <c>ready</c>, <c>in_progress</c>, <c>review</c> or <c>done</c>.
/// A card with a missing field, a bad id or any other state is left out and counted in <see cref="SkippedTaskCount"/>.
/// A card may carry an optional <c>triggers</c> array of RyuseiFuda micro triggers (doc 38, layer 1 chips):
/// <c>{ "id", "kind", "state", "nextFire", "purpose" }</c>. A malformed chip is left out; the card stays.
/// </summary>
public sealed class KanbanPaneViewModel : IPaneViewModel
{
    private static readonly string[] _states = ["backlog", "ready", "in_progress", "review", "done"];

    private readonly Dictionary<Guid, IReadOnlyList<RyuseiTaskChip>> _chips = [];

    /// <param name="descriptor">The board pane descriptor, with the board snapshot as its model.</param>
    /// <param name="triggersByTask">
    /// The owner's live chips by task id, from <see cref="RyuseiChipRules.ByTask"/>. When given, these are the chips shown on each
    /// card, and any <c>triggers</c> array in the snapshot is ignored. When null, the snapshot's array is read as before.
    /// </param>
    public KanbanPaneViewModel(PaneDescriptor descriptor, IReadOnlyDictionary<Guid, IReadOnlyList<RyuseiTaskChip>>? triggersByTask = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = descriptor.Title;

        var cards = new List<KanbanBoardCard>();
        var skipped = 0;
        foreach (var element in PaneValues.Array(descriptor.Model, "cards").EnumerateArray())
        {
            if (TryCard(element, out var card))
            {
                cards.Add(card);
                _chips[card.Id] = triggersByTask is null
                    ? RyuseiChipRules.Visible(ReadChips(element))
                    : triggersByTask.TryGetValue(card.Id, out var joined) ? joined : [];
            }
            else
            {
                skipped++;
            }
        }

        // The board does the Blocked derivation and the column order; the pane only reads its result.
        var board = KanbanBoardMapping.ToBoard(new KanbanBoardSnapshot(Guid.Empty, cards));
        Columns = board.Columns
            .Select(column => new KanbanColumn(
                column.Title,
                column.Cards.Select(card => new KanbanCard(card.Id, card.Title, card.Blocked)).ToList()))
            .ToArray();
        SkippedTaskCount = skipped;
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>All five columns, always in board order, even when empty.</summary>
    public IReadOnlyList<KanbanColumn> Columns { get; }

    /// <summary>Cards left out because a field was missing or malformed, or their state was not one of the five.</summary>
    public int SkippedTaskCount { get; }

    /// <summary>The live trigger chips of one card (active and paused only), or none when the card has none.</summary>
    public IReadOnlyList<RyuseiTaskChip> ChipsFor(Guid cardId) =>
        _chips.TryGetValue(cardId, out var chips) ? chips : [];

    /// <summary>Reads the optional <c>triggers</c> array. A missing array reads as empty; a bad chip is left out.</summary>
    private static List<RyuseiTaskChip> ReadChips(JsonElement card)
    {
        var chips = new List<RyuseiTaskChip>();
        if (!card.TryGetProperty("triggers", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return chips;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (TryChip(element, out var chip))
            {
                chips.Add(chip);
            }
        }

        return chips;
    }

    private static bool TryChip(JsonElement element, out RyuseiTaskChip chip)
    {
        chip = null!;
        if (element.ValueKind != JsonValueKind.Object ||
            !TryText(element, "id", out var id) ||
            !TryText(element, "kind", out var kind) ||
            !TryText(element, "state", out var state) ||
            !TryText(element, "purpose", out var purpose))
        {
            return false;
        }

        DateTimeOffset? nextFire = null;
        if (element.TryGetProperty("nextFire", out var fire) && fire.ValueKind != JsonValueKind.Null)
        {
            if (fire.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(fire.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return false;
            }

            nextFire = parsed;
        }

        chip = new RyuseiTaskChip(id, kind, state, nextFire, purpose);
        return true;
    }

    private static bool TryText(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryCard(JsonElement element, out KanbanBoardCard card)
    {
        card = null!;
        if (element.ValueKind != JsonValueKind.Object ||
            !TryGuid(element, "id", out var id) ||
            !element.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
            Array.IndexOf(_states, state.GetString()) < 0)
        {
            return false;
        }

        Guid? owner = null;
        if (element.TryGetProperty("ownerAgentId", out var ownerElement) && ownerElement.ValueKind != JsonValueKind.Null)
        {
            if (ownerElement.ValueKind != JsonValueKind.String || !Guid.TryParse(ownerElement.GetString(), out var parsed))
            {
                return false;
            }

            owner = parsed;
        }

        if (!TryGuidArray(element, "unfinishedBlockerIds", out var blockers) ||
            !TryStringArray(element, "blockerTitles", out var blockerTitles))
        {
            return false;
        }

        card = new KanbanBoardCard(id, title.GetString()!, state.GetString()!, owner, blockers, blockerTitles);
        return true;
    }

    private static bool TryGuid(JsonElement element, string name, out Guid value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               Guid.TryParse(property.GetString(), out value);
    }

    /// <summary>A missing array reads as empty; a present one must hold only Guid strings.</summary>
    private static bool TryGuidArray(JsonElement element, string name, out IReadOnlyList<Guid> values)
    {
        var result = new List<Guid>();
        values = result;
        if (!element.TryGetProperty(name, out var array))
        {
            return true;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id))
            {
                return false;
            }

            result.Add(id);
        }

        return true;
    }

    /// <summary>A missing array reads as empty; a present one must hold only strings.</summary>
    private static bool TryStringArray(JsonElement element, string name, out IReadOnlyList<string> values)
    {
        var result = new List<string>();
        values = result;
        if (!element.TryGetProperty(name, out var array))
        {
            return true;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            result.Add(item.GetString()!);
        }

        return true;
    }
}
