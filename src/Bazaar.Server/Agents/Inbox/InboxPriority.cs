namespace Bazaar.Server.Agents.Inbox;

/// <summary>The wake priority order from doc 06 ("Wake model"). A lower rank is a higher priority.</summary>
public static class InboxPriority
{
    /// <summary>The rank of an inbox kind; lower is higher priority.</summary>
    /// <exception cref="ArgumentException">The kind is not a known <see cref="InboxKind"/>.</exception>
    public static int Rank(string kind) => kind switch
    {
        InboxKind.Interrupt => 0,
        InboxKind.Mention => 1,
        InboxKind.Assignment => 2,
        InboxKind.VoteRequest => 3,
        InboxKind.Scheduled => 4,
        InboxKind.Brainstorm => 5,
        InboxKind.SkimmerPromoted => 6,
        InboxKind.GcDigest => 7,
        InboxKind.Constitution => 8,
        _ => throw new ArgumentException($"Unknown inbox kind '{kind}'.", nameof(kind)),
    };

    /// <summary>True when <paramref name="kind"/> is a known <see cref="InboxKind"/>.</summary>
    public static bool IsKnown(string kind) => kind is not null && InboxKind.All.Contains(kind);
}
