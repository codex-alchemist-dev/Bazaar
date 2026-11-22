using Bazaar.Server.Council;
using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;

namespace Bazaar.Server.Council.Hiring.Tests;

public class HireMotionDraftBuilderTests
{
    [Fact]
    public void Builds_an_approve_reject_hire_motion_with_the_template_as_subject()
    {
        var builder = new HireMotionDraftBuilder(new HireMotionClassifier());
        var request = Samples.Request("talent-analyst-v2");

        var motion = builder.Build(request);

        Assert.Equal(MembershipMotionKind.Hire, motion.Kind);
        Assert.Equal(VoteKind.ApproveReject, motion.Draft.Kind);
        Assert.Equal("talent-analyst-v2", motion.Draft.SubjectRef);
        Assert.Equal("talent-analyst-v2", motion.Draft.MembershipSubjectId);
        Assert.Equal(MembershipMotionKind.Hire, motion.Draft.MembershipKind);
        Assert.Empty(motion.Draft.Options);
        Assert.Equal(Samples.GcId, motion.Draft.GcId);
        Assert.Equal("agent-proposer", motion.Draft.ProposerId);
        Assert.Equal("agent-author", motion.Draft.SubjectAuthorId);
        Assert.Equal(request.Deadline, motion.Draft.Deadline);
    }

    [Fact]
    public void Motion_class_follows_the_classifier()
    {
        var bigger = new HireMotionDraftBuilder(new HireMotionClassifier(["hire"])).Build(Samples.Request());
        var ordinary = new HireMotionDraftBuilder(new HireMotionClassifier(["retire"])).Build(Samples.Request());

        Assert.Equal(MotionClass.BiggerChange, bigger.Draft.MotionClass);
        Assert.Equal(MotionClass.Ordinary, ordinary.Draft.MotionClass);
    }

    [Fact]
    public void Author_may_be_unknown()
    {
        var builder = new HireMotionDraftBuilder(new HireMotionClassifier());
        var request = Samples.Request() with { SubjectAuthorId = null };

        Assert.Null(builder.Build(request).Draft.SubjectAuthorId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_template_id_is_rejected(string templateId)
    {
        var builder = new HireMotionDraftBuilder(new HireMotionClassifier());

        Assert.Throws<ArgumentException>(() => builder.Build(Samples.Request(templateId)));
    }

    [Fact]
    public void Blank_proposer_is_rejected()
    {
        var builder = new HireMotionDraftBuilder(new HireMotionClassifier());

        Assert.Throws<ArgumentException>(() => builder.Build(Samples.Request(proposer: " ")));
    }

    [Fact]
    public void Empty_gc_id_is_rejected()
    {
        var builder = new HireMotionDraftBuilder(new HireMotionClassifier());
        var request = Samples.Request() with { GcId = Guid.Empty };

        Assert.Throws<ArgumentException>(() => builder.Build(request));
    }

    [Fact]
    public void Null_request_is_rejected()
    {
        var builder = new HireMotionDraftBuilder(new HireMotionClassifier());

        Assert.Throws<ArgumentNullException>(() => builder.Build(null!));
    }
}
