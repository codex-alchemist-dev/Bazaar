using System.Collections.Immutable;

namespace Bazaar.Server.Fun.Lucky;

/// <summary>An agent the controller may invite.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="OnShift">True when the agent is on shift now.</param>
/// <param name="OptedIn">True when the agent opted in to Lucky runs.</param>
public sealed record LuckyCandidate(string AgentId, bool OnShift, bool OptedIn);

/// <summary>A participant with the persona seed recorded for the run.</summary>
/// <param name="AgentId">The participant.</param>
/// <param name="Seed">The recorded random seed; the persona is reproduced from it.</param>
/// <param name="Persona">The persona the seed selects.</param>
public sealed record LuckyParticipant(string AgentId, int Seed, string Persona);

/// <summary>Whether an idea is kept as text or quarantined.</summary>
public enum LuckyIdeaStatus
{
    /// <summary>Kept as an <c>idea</c> entry with source trust <c>agent</c>.</summary>
    Kept,

    /// <summary>A secret-shaped string was found; the idea is quarantined and not shown.</summary>
    Quarantined,
}

/// <summary>One idea recorded in a run.</summary>
/// <param name="AgentId">The author.</param>
/// <param name="Text">The sanitized idea text; empty when quarantined.</param>
/// <param name="Status">Kept or quarantined.</param>
/// <param name="OwnerMarked">True when the owner marked the idea for a Backlog card.</param>
public sealed record LuckyIdea(string AgentId, string Text, LuckyIdeaStatus Status, bool OwnerMarked = false);

/// <summary>An open or finished Lucky run.</summary>
/// <param name="RunId">The run's identifier.</param>
/// <param name="Prompt">The owner's prompt.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="EndsAt">When the time box ends.</param>
/// <param name="Participants">The invited agents with their recorded seeds.</param>
/// <param name="Ideas">Ideas in the order they were recorded.</param>
public sealed record LuckyRun(
    string RunId,
    string Prompt,
    DateTimeOffset StartedAt,
    DateTimeOffset EndsAt,
    ImmutableArray<LuckyParticipant> Participants,
    ImmutableArray<LuckyIdea> Ideas)
{
    /// <summary>True while the time box is open at <paramref name="now"/>.</summary>
    public bool IsOpen(DateTimeOffset now) => now < EndsAt;
}

/// <summary>The result of a controller request.</summary>
/// <param name="Accepted">True when the request was applied.</param>
/// <param name="Run">The run after the request; unchanged when refused.</param>
/// <param name="Refusal">Why the request was refused, or null.</param>
public sealed record LuckyResult(bool Accepted, LuckyRun? Run, string? Refusal)
{
    /// <summary>A refused request.</summary>
    public static LuckyResult Refuse(string reason, LuckyRun? run) => new(false, run, reason);

    /// <summary>An applied request.</summary>
    public static LuckyResult Accept(LuckyRun run) => new(true, run, null);
}
