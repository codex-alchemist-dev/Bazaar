namespace Bazaar.Server.Knowledge.CompanyMemory;

/// <summary>One decision record as the chain needs it: its Vault path and the front matter fields that link decisions.</summary>
/// <param name="Path">The Vault path, such as <c>decisions/0002-use-notify.md</c>.</param>
/// <param name="Status">The record's status: <c>proposed</c>, <c>accepted</c> or <c>superseded</c>.</param>
/// <param name="Supersedes">Decisions this record replaces, as written in front matter (<c>0001-slug</c> or a path).</param>
/// <param name="SupersededBy">The decision that replaces this one, as written in front matter, or null.</param>
/// <param name="DecidedIn">The GC message reference that decided the record, or null. Not used by the chain itself.</param>
public sealed record DecisionRecord(
    string Path,
    string Status,
    IReadOnlyList<string> Supersedes,
    string? SupersededBy,
    string? DecidedIn);

/// <summary>
/// One edge of the decision chain (doc 25 <c>decision_links</c>). <see cref="FromDoc"/> is the newer decision and
/// <see cref="ToDoc"/> the one it replaces, so every edge reads "from supersedes to".
/// </summary>
/// <param name="FromDoc">The Vault path of the superseding decision.</param>
/// <param name="ToDoc">The Vault path of the superseded decision. Set even when that record is missing.</param>
/// <param name="Relation">Always <see cref="DecisionChain.SupersedesRelation"/> for now.</param>
/// <param name="Status">The status of <see cref="FromDoc"/> when that record is in the input; null when it is missing.</param>
/// <param name="FromMissing">True when <see cref="FromDoc"/> is not among the decision records (a "missing source").</param>
/// <param name="ToMissing">True when <see cref="ToDoc"/> is not among the decision records (an unresolved target).</param>
public sealed record DecisionLink(
    string FromDoc,
    string ToDoc,
    string Relation,
    string? Status,
    bool FromMissing,
    bool ToMissing);
