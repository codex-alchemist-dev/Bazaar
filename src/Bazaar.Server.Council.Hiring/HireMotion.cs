using Bazaar.Server.Council.Membership;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Hiring;

/// <summary>
/// The Council motion for a hire. The draft itself carries the membership kind and subject, which the vote stores
/// for the outcome handoff; <see cref="Kind"/> repeats the kind for callers.
/// </summary>
/// <param name="Draft">The motion to open with <see cref="ICouncilService.OpenAsync"/>.</param>
/// <param name="Kind">Always <see cref="MembershipMotionKind.Hire"/>.</param>
public sealed record HireMotion(MotionDraft Draft, MembershipMotionKind Kind);
