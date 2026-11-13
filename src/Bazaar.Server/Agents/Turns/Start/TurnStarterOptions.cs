namespace Bazaar.Server.Agents.Turns.Start;

/// <summary>Settings for <see cref="TurnStarter"/>.</summary>
public sealed class TurnStarterOptions
{
    /// <summary>
    /// The chain depth used for a <c>ryusei_*</c> item whose depth cannot be read. Fail closed: an unreadable fire depth is
    /// treated as the maximum so the turn cannot schedule deeper than the fire could have.
    /// </summary>
    public int UnknownRyuseiDepth { get; init; } = 3;
}
