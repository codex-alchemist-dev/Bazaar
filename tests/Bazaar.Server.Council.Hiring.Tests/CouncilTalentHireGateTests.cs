using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Council.Hiring.Tests;

public class CouncilTalentHireGateTests
{
    private readonly FakeCouncilService _council = new();
    private readonly InMemoryHireDecisionStore _store = new();
    private readonly FakeTimeProvider _time = new(Samples.Now);

    private CouncilTalentHireGate Gate(string[]? classes = null) => new(
        _council,
        _store,
        new HireMotionDraftBuilder(new HireMotionClassifier(classes)),
        _time);

    [Fact]
    public async Task Request_opens_one_hire_vote_on_the_council_and_records_it_as_pending()
    {
        var gate = Gate();

        var state = await gate.RequestAsync(Samples.Request("talent-analyst-v2"), CancellationToken.None);

        var opened = Assert.Single(_council.Opened);
        Assert.Equal("agent-proposer", opened.Caller);
        Assert.Equal("talent-analyst-v2", opened.Draft.SubjectRef);
        Assert.Equal(opened.VoteId, state.VoteId);
        Assert.Equal(HireStatus.Pending, state.Status);
        Assert.Null(state.Decision);
        Assert.Equal(Samples.Now, state.UpdatedAt);
        Assert.False(await gate.IsReleasedAsync("talent-analyst-v2", CancellationToken.None));
    }

    [Fact]
    public async Task A_second_request_for_a_pending_template_opens_no_second_vote()
    {
        var gate = Gate();
        var first = await gate.RequestAsync(Samples.Request(), CancellationToken.None);

        var second = await gate.RequestAsync(Samples.Request(), CancellationToken.None);

        Assert.Single(_council.Opened);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task A_released_template_returns_its_state_without_a_new_vote()
    {
        var gate = Gate();
        var pending = await gate.RequestAsync(Samples.Request(), CancellationToken.None);
        await new HireOutcomeSink(_store, _time).PublishAsync(
            Samples.Outcome(pending.VoteId, "talent-analyst-v2", ApprovalChoices.Yes), CancellationToken.None);

        var again = await gate.RequestAsync(Samples.Request(), CancellationToken.None);

        Assert.Single(_council.Opened);
        Assert.Equal(HireStatus.Released, again.Status);
    }

    [Fact]
    public async Task A_blocked_template_can_be_proposed_again_and_opens_a_new_vote()
    {
        var gate = Gate();
        var first = await gate.RequestAsync(Samples.Request(), CancellationToken.None);
        await new HireOutcomeSink(_store, _time).PublishAsync(
            Samples.Outcome(first.VoteId, "talent-analyst-v2", ApprovalChoices.No), CancellationToken.None);

        var second = await gate.RequestAsync(Samples.Request(), CancellationToken.None);

        Assert.Equal(2, _council.Opened.Count);
        Assert.NotEqual(first.VoteId, second.VoteId);
        Assert.Equal(HireStatus.Pending, second.Status);
    }

    [Fact]
    public async Task Template_with_no_record_is_not_released()
    {
        var gate = Gate();

        Assert.False(await gate.IsReleasedAsync("never-proposed", CancellationToken.None));
    }

    [Fact]
    public async Task Hire_motion_class_comes_from_the_configured_classes()
    {
        var gate = Gate(classes: []);

        await gate.RequestAsync(Samples.Request(), CancellationToken.None);

        Assert.Equal(MotionClass.Ordinary, Assert.Single(_council.Opened).Draft.MotionClass);
    }

    [Fact]
    public async Task Default_classes_open_the_hire_as_a_bigger_change()
    {
        var gate = Gate();

        await gate.RequestAsync(Samples.Request(), CancellationToken.None);

        Assert.Equal(MotionClass.BiggerChange, Assert.Single(_council.Opened).Draft.MotionClass);
    }

    [Fact]
    public async Task Null_request_is_rejected()
    {
        var gate = Gate();

        await Assert.ThrowsAsync<ArgumentNullException>(() => gate.RequestAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Blank_template_in_the_release_check_is_rejected()
    {
        var gate = Gate();

        await Assert.ThrowsAsync<ArgumentException>(() => gate.IsReleasedAsync(" ", CancellationToken.None));
    }
}
