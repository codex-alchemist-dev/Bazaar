namespace Bazaar.Server.Skimmer.Rules;

/// <summary>
/// The result of the rules stage. When <see cref="Decided"/> is false the scored stage must look at the message, and
/// <see cref="Path"/> and <see cref="Decision"/> are null.
/// </summary>
/// <param name="Path">A <see cref="GatePaths"/> value, or null when undecided.</param>
/// <param name="Decision">A <see cref="GateDecisions"/> value, or null when undecided.</param>
/// <param name="Decided">True when a rule matched.</param>
public sealed record RuleResult(string? Path, string? Decision, bool Decided)
{
    /// <summary>The result for a message no rule matched.</summary>
    public static RuleResult Undecided { get; } = new(null, null, false);

    /// <summary>A decided result for the given path and decision.</summary>
    public static RuleResult Of(string path, string decision) => new(path, decision, true);
}
