namespace Bazaar.Server.Fun.Lucky;

/// <summary><c>lucky.participation</c> values.</summary>
public enum LuckyParticipation
{
    /// <summary>Agents on shift, plus agents who opted in.</summary>
    OnShiftOrOptedIn,

    /// <summary>Only agents who opted in.</summary>
    OptedInOnly,
}

/// <summary><c>lucky.cards_from_ideas</c> values.</summary>
public enum LuckyCardsFromIdeas
{
    /// <summary>Only ideas the owner marked become Backlog cards.</summary>
    OwnerMarked,

    /// <summary>No ideas become cards.</summary>
    None,
}

/// <summary>The Lucky settings from doc 26 (<c>lucky.*</c>) with the shipped defaults.</summary>
public sealed class LuckySettings
{
    /// <summary><c>lucky.run_minutes</c>: the time box for one run.</summary>
    public int RunMinutes { get; init; } = 20;

    /// <summary><c>lucky.max_ideas</c>: ideas per agent per run.</summary>
    public int MaxIdeas { get; init; } = 5;

    /// <summary><c>lucky.max_runs_per_day</c>: runs allowed per day.</summary>
    public int MaxRunsPerDay { get; init; } = 1;

    /// <summary><c>lucky.participation</c>: who is invited.</summary>
    public LuckyParticipation Participation { get; init; } = LuckyParticipation.OnShiftOrOptedIn;

    /// <summary><c>lucky.cards_from_ideas</c>: whether owner-marked ideas become cards.</summary>
    public LuckyCardsFromIdeas CardsFromIdeas { get; init; } = LuckyCardsFromIdeas.OwnerMarked;

    /// <summary>Checks that every limit is positive.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is below one.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(RunMinutes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxIdeas, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRunsPerDay, 1);
    }
}
