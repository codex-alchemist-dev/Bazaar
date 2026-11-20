using Bazaar.Client.RyuseiFuda.Phone;
using Bazaar.Client.RyuseiFuda.Workday;

namespace Bazaar.Client.Core.Tests.Kanban.Phone;

/// <summary>A move the fake received, as the pane sent it.</summary>
internal sealed record MoveCall(Guid CardId, string ToState, long ExpectedRevision);

/// <summary>
/// An in-memory stand-in for the board service. It applies a move only when the revision matches, refuses a card with an
/// unfinished prerequisite, and reports a conflict otherwise, the way the server's transition does (doc 12).
/// </summary>
internal sealed class FakeKanbanBoardClient : IKanbanBoardClient
{
    private readonly Dictionary<Guid, KanbanPhoneCard> _server = [];

    public FakeKanbanBoardClient(params KanbanPhoneCard[] cards)
    {
        foreach (var card in cards)
        {
            _server[card.Id] = card;
        }
    }

    /// <summary>Every move the pane sent, in order.</summary>
    public List<MoveCall> Moves { get; } = [];

    /// <summary>Count of board loads, so a test can see a reload happen.</summary>
    public int LoadCalls { get; private set; }

    /// <summary>When true, loads throw, as a dropped connection would.</summary>
    public bool FailLoads { get; set; }

    /// <summary>When set, the next move waits on this source instead of being decided, so a test can hold a move in flight.</summary>
    public TaskCompletionSource<KanbanMoveResult>? HeldMove { get; set; }

    /// <summary>When set, every move returns this result without changing the server, for outcomes the fake does not decide itself.</summary>
    public KanbanMoveResult? ForcedResult { get; set; }

    /// <summary>Changes a card on the server as another writer would, so the pane's copy goes stale.</summary>
    public void ChangeOnServer(KanbanPhoneCard card) => _server[card.Id] = card;

    /// <summary>The server's copy of a card.</summary>
    public KanbanPhoneCard ServerCard(Guid id) => _server[id];

    public Task<KanbanPhoneBoard> LoadAsync(Guid projectId, CancellationToken ct)
    {
        LoadCalls++;
        if (FailLoads)
        {
            return Task.FromException<KanbanPhoneBoard>(new HttpRequestException("connection dropped"));
        }

        return Task.FromResult(new KanbanPhoneBoard(projectId, _server.Values.ToList()));
    }

    public Task<KanbanMoveResult> MoveAsync(Guid projectId, Guid cardId, string toState, long expectedRevision, CancellationToken ct)
    {
        Moves.Add(new MoveCall(cardId, toState, expectedRevision));
        if (HeldMove is not null)
        {
            return HeldMove.Task;
        }

        return Task.FromResult(ForcedResult ?? Decide(cardId, toState, expectedRevision));
    }

    private KanbanMoveResult Decide(Guid cardId, string toState, long expectedRevision)
    {
        if (!_server.TryGetValue(cardId, out var card))
        {
            return new KanbanMoveResult.Invalid("That card does not exist.");
        }

        if (card.Revision != expectedRevision)
        {
            return new KanbanMoveResult.Conflict(card.Revision);
        }

        if (card.Blocked)
        {
            return new KanbanMoveResult.Refused(card.BlockerTitles, "Finish the prerequisites first.");
        }

        var moved = card with { State = toState, Revision = card.Revision + 1 };
        _server[cardId] = moved;
        return new KanbanMoveResult.Applied(moved.Revision);
    }
}

/// <summary>Card builders shared by the phone pane tests.</summary>
internal static class PhoneCards
{
    public static readonly Guid ProjectId = Guid.Parse("0196a6f0-0000-7000-8000-000000000900");

    public static Guid Id(int n) => Guid.Parse($"0196a6f0-0000-7000-8000-0000000001{n:00}");

    public static KanbanPhoneCard Card(int n, string title, string state, long revision = 1, params string[] blockerTitles) =>
        new(
            Id(n),
            title,
            state,
            revision,
            blockerTitles.Select((_, i) => Guid.Parse($"0196a6f0-0000-7000-8000-0000000009{i:00}")).ToList(),
            blockerTitles);
}
