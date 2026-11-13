using System.Text.RegularExpressions;

namespace Bazaar.Diagnostics.Redaction;

/// <summary>
/// One pattern rule. The evaluator returns the replacement for each match, so a rule can keep part of a
/// match (for example a loopback address or the key of a key/value pair). Returning the match unchanged
/// means "not a hit".
/// </summary>
public sealed record RedactionRule(string Id, Regex Pattern, MatchEvaluator Evaluator);
