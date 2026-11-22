namespace Bazaar.Server.Skimmer.Rules;

/// <summary>The rules-stage settings (doc 13, "Settings"). Only the rules stage's own keys are here.</summary>
/// <param name="DmAlwaysWakes">The value of <c>skimmer.dm_always_wakes</c>. A DM with the agent wakes when true.</param>
public sealed record GateRuleSettings(bool DmAlwaysWakes = true);
