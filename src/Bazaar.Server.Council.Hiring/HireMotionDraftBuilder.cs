using Bazaar.Server.Council.Membership;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Hiring;

/// <summary>
/// Builds the approve/reject <see cref="MotionDraft"/> for a Talent hire (docs/design/10-council-and-voting.md, Membership
/// and role votes; docs/design/25-company-systems-knowledge.md). The subject is the template id, and the membership subject
/// is the same string, so no agent id is invented before instantiation.
/// </summary>
public sealed class HireMotionDraftBuilder(HireMotionClassifier classifier)
{
    private readonly HireMotionClassifier _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));

    /// <summary>Builds the hire motion for a request.</summary>
    /// <exception cref="ArgumentException">The template id or proposer is blank, or the GC id is empty.</exception>
    public HireMotion Build(HireRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TemplateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProposerId);
        if (request.GcId == Guid.Empty)
        {
            throw new ArgumentException("A hire vote runs in a GC.", nameof(request));
        }

        var draft = new MotionDraft(
            SubjectRef: request.TemplateId,
            Kind: VoteKind.ApproveReject,
            MotionClass: _classifier.Classify(MembershipMotionKind.Hire),
            Options: [],
            GcId: request.GcId,
            ProposerId: request.ProposerId,
            SubjectAuthorId: request.SubjectAuthorId,
            Deadline: request.Deadline,
            MembershipSubjectId: request.TemplateId);

        return new HireMotion(draft, MembershipMotionKind.Hire);
    }
}
