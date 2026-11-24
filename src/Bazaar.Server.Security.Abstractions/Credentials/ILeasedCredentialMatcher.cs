namespace Bazaar.Server.Security.Credentials;

/// <summary>
/// Answers whether text contains a credential currently leased to an agent (doc 15, "Reads for modules"; doc 59 section 5,
/// rule 3). It never returns the value, the lease or where the value matched.
/// </summary>
/// <remarks>
/// A lease counts while it is issued, not expired and not revoked. Matching is an exact, case-sensitive match on the UTF-8
/// bytes of the value; encoded forms are not matched (doc 15, question 5). An implementation that cannot answer throws, so
/// the caller refuses.
/// </remarks>
public interface ILeasedCredentialMatcher
{
    /// <summary>True when <paramref name="text"/> contains the value of a credential currently leased to the agent.</summary>
    /// <param name="agentId">The agent. Must not be blank.</param>
    /// <param name="text">The text to check. Empty text contains nothing.</param>
    /// <param name="ct">Cancels the check.</param>
    ValueTask<bool> ContainsLeasedCredentialAsync(string agentId, string text, CancellationToken ct = default);
}
