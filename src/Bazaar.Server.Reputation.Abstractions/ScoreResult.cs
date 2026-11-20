namespace Bazaar.Server.Reputation;

/// <summary>
/// Score for one (agent, tag). <c>NEff</c> is the decayed evidence weight sum, excluding the prior.
/// </summary>
public sealed record ScoreResult(double S, double Lcb, double NEff, int WeightsVersion);
