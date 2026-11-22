namespace Bazaar.Server.Skimmer;

/// <summary>
/// The port to the gateway's model policy for the <c>gatekeeper</c> subject (doc 05, "Policy"). The host adapts it onto
/// <c>agent_model_policy</c>, so this module holds no gateway SQL.
/// </summary>
public interface IGatekeeperPolicySeed
{
    /// <summary>
    /// Makes sure the <c>gatekeeper</c> subject may use exactly <paramref name="model"/>. A row an operator edited is never
    /// overwritten. Returns true when a row was written.
    /// </summary>
    Task<bool> EnsureAsync(string model, CancellationToken ct);
}
