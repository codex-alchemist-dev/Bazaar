using Bazaar.Server.Council.Membership;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Hiring;

/// <summary>
/// The Council motion for a hire, with the membership kind the outcome path needs. <see cref="MotionDraft"/> has no
/// membership kind field, so the kind travels beside the draft and is recorded as the vote's membership metadata.
/// </summary>
/// <param name="Draft">The motion to open with <see cref="ICouncilService.OpenAsync"/>.</param>
/// <param name="Kind">Always <see cref="MembershipMotionKind.Hire"/>.</param>
public sealed record HireMotion(MotionDraft Draft, MembershipMotionKind Kind);
