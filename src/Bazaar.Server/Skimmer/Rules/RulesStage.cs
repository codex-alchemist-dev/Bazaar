namespace Bazaar.Server.Skimmer.Rules;

/// <summary>
/// The synchronous, deterministic rules stage of the Skimmer Bouncer (doc 13, "Placement" step 3). It is a pure function:
/// no I/O, no clock and no mutable state. Precedence is first match wins: quarantine, mention, DM, critical.
/// </summary>
public static class RulesStage
{
    /// <summary>Decides one message for one agent by the rules alone.</summary>
    /// <param name="facts">The message facts for one agent.</param>
    /// <param name="settings">The rules-stage settings.</param>
    /// <returns>A decided result when a rule matched; <see cref="RuleResult.Undecided"/> otherwise.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="facts"/>, <paramref name="settings"/>, or a collection on <paramref name="facts"/> is null.</exception>
    public static RuleResult Evaluate(GcMessageFacts facts, GateRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(facts.AgentHandles);
        ArgumentNullException.ThrowIfNull(facts.MentionedHandles);

        if (facts.SenderQuarantined)
        {
            return RuleResult.Of(GatePaths.Quarantine, GateDecisions.Defer);
        }

        if (IsMentioned(facts))
        {
            return RuleResult.Of(GatePaths.Mention, GateDecisions.Wake);
        }

        if (facts.IsDmWithAgent && settings.DmAlwaysWakes)
        {
            return RuleResult.Of(GatePaths.Dm, GateDecisions.Wake);
        }

        if (facts.IsCritical || facts.IsOwnerAuthor)
        {
            return RuleResult.Of(GatePaths.Critical, GateDecisions.Wake);
        }

        return RuleResult.Undecided;
    }

    private static bool IsMentioned(GcMessageFacts facts)
    {
        foreach (var mentioned in facts.MentionedHandles)
        {
            foreach (var handle in facts.AgentHandles)
            {
                if (string.Equals(mentioned, handle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
