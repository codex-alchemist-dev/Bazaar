namespace Bazaar.Server.Security.Taint;

/// <summary>
/// Pure taint rules: how each piece of content changes a session, which content is delivered at all,
/// and when hard taint gates Tier 2. No I/O, so the rule table is fully testable.
/// </summary>
public static class TaintRules
{
    /// <summary>The taint level one piece of content adds to a session, before the session's own level is considered.</summary>
    public static TaintState Contribution(TaintInput input, TaintPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (input.InheritsExternal)
        {
            return TaintState.Hard;
        }

        return input.Trust switch
        {
            // External content taints hard (doc 15). Quarantined content is never delivered, so it adds nothing.
            ContentTrust.External => TaintState.Hard,
            ContentTrust.Quarantined => TaintState.Clean,

            // Agent text taints softly, and only when the source that produced it was tainted.
            ContentTrust.Agent => input.SourceState == TaintState.Clean ? TaintState.Clean : TaintState.Soft,

            ContentTrust.MemberHuman => policy.MemberHumanTaints ? TaintState.Soft : TaintState.Clean,

            ContentTrust.Owner or ContentTrust.System => TaintState.Clean,

            _ => throw new ArgumentOutOfRangeException(nameof(input), input.Trust, "Unknown trust tag."),
        };
    }

    /// <summary>
    /// Whether content with this tag may be delivered to an agent's context at all. Quarantined content
    /// never is; the original stays readable for audit (doc 01).
    /// </summary>
    public static bool IsDelivered(ContentTrust trust) => trust != ContentTrust.Quarantined;

    /// <summary>
    /// Adds one piece of content to a session. Delivery is refused for quarantined content, which leaves the
    /// session unchanged. Otherwise the level is the higher of the current level and the contribution, and
    /// every contributing source is recorded once, in arrival order.
    /// </summary>
    public static SessionTaint Apply(SessionTaint session, TaintInput input, TaintPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!IsDelivered(input.Trust))
        {
            return session;
        }

        var contribution = Contribution(input, policy);
        if (contribution == TaintState.Clean)
        {
            return session;
        }

        var state = (TaintState)Math.Max((int)session.State, (int)contribution);
        var sources = session.Sources;
        var alreadyRecorded = sources.Any(source => source.SourceId == input.SourceId);
        if (!alreadyRecorded)
        {
            var trust = input.InheritsExternal ? ContentTrust.External : input.Trust;
            sources = sources.Add(new TaintSource(input.SourceId, trust, contribution));
        }

        return session with { State = state, Sources = sources };
    }

    /// <summary>Hard taint blocks every Tier 2 capability without per-action approval. Soft taint does not.</summary>
    public static bool BlocksTier2(TaintState state) => state == TaintState.Hard;
}
