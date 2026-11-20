using Bazaar.Server.Council.Membership;

namespace Bazaar.Server.Council.Hiring;

/// <summary>Where a Talent template's hire vote stands.</summary>
public enum HireStatus
{
    /// <summary>A hire vote is open. The template stays quarantined.</summary>
    Pending,

    /// <summary>The latest hire vote passed with an explicit approve. The template may leave quarantine.</summary>
    Released,

    /// <summary>The latest hire vote was rejected or withdrawn. The template stays quarantined.</summary>
    Blocked,
}

/// <summary>The current hire state of one Talent template.</summary>
/// <param name="TemplateId">The Talent template id.</param>
/// <param name="VoteId">The hire vote this state belongs to.</param>
/// <param name="Status">Pending, released or blocked.</param>
/// <param name="Decision">The Council decision once decided; null while pending.</param>
/// <param name="UpdatedAt">When the state last changed.</param>
/// <remarks>
/// The record carries no tool grant. A Tier 2 tool grant is a separate approval (docs/design/25, docs/design/15), and the hire
/// vote never implies one.
/// </remarks>
public sealed record HireVoteState(
    string TemplateId,
    Guid VoteId,
    HireStatus Status,
    MembershipDecision? Decision,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The quarantine gate for Talent hires. The local stand-in for ITalentHireGate, which is not available yet.
/// Fails closed: a template is released only when its latest hire vote passed with an explicit approve.
/// </summary>
public interface IHireGate
{
    /// <summary>
    /// Opens a hire vote for the template, unless one is already pending or the template is already released. A blocked
    /// template may be proposed again, which opens a new vote.
    /// </summary>
    /// <exception cref="ArgumentException">The request is invalid.</exception>
    Task<HireVoteState> RequestAsync(HireRequest request, CancellationToken ct);

    /// <summary>True only when the template's latest hire vote passed. No record means false.</summary>
    Task<bool> IsReleasedAsync(string templateId, CancellationToken ct);
}
