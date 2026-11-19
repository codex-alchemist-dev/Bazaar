using Bazaar.Server.Modules.Updates;

namespace Bazaar.Server.Modules.Persistence;

/// <summary>
/// One update decision for one candidate, as stored in <c>module_update_decision</c> (<c>modules/0001</c>). Decisions are
/// append-only: a later decision for the same module adds a row and never replaces one.
/// </summary>
/// <param name="ModuleId">The module the candidate belongs to.</param>
/// <param name="Version">The candidate version, for example "18.1.2".</param>
/// <param name="Line">The major line the version belongs to.</param>
/// <param name="Decision">What the evaluator or a person decided.</param>
/// <param name="Reason">Why, in words a person can read.</param>
/// <param name="PolicyId">The policy that set the decision, or null when the default rules did.</param>
/// <param name="DecidedAt">When the decision was made.</param>
/// <param name="Actor">Who made it: the evaluator, or the person who approved or rejected.</param>
public sealed record ModuleUpdateDecisionRecord(
    string ModuleId,
    string Version,
    string Line,
    UpdateDecision Decision,
    string Reason,
    string? PolicyId,
    DateTimeOffset DecidedAt,
    string Actor);

