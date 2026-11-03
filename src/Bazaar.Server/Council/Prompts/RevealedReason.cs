namespace Bazaar.Server.Council.Prompts;

/// <summary>
/// One reason as revealed to a voter in the follow-up round. The label is a per-vote alias (see <see cref="ReasonOrder"/>),
/// never an agent id. The list holding these is in the stored reason order.
/// </summary>
/// <param name="Label">Alias such as "Reason A".</param>
/// <param name="Reasoning">The reasoning text as written in the independent ballot.</param>
public sealed record RevealedReason(string Label, string Reasoning);
