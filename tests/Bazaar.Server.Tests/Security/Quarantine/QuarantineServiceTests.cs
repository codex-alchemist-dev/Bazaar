using Bazaar.Server.Security.Quarantine;

namespace Bazaar.Server.Tests.Security.Quarantine;

public class QuarantineServiceTests
{
    private static readonly QuarantineActor _owner = new("owner-1", QuarantineActorKind.Owner);
    private static readonly QuarantineActor _agent = new("agent-a", QuarantineActorKind.Agent);
    private static readonly QuarantineActor _reader = new("reader-1", QuarantineActorKind.Reader);
    private static readonly QuarantineActor _system = new("system", QuarantineActorKind.System);

    private readonly InMemoryQuarantineStore _store = new();
    private readonly RecordingAgentTokenRevoker _revoker = new();
    private readonly RecordingVmPauser _pauser = new();
    private readonly QuarantineService _service;

    public QuarantineServiceTests()
    {
        _service = new QuarantineService(_store, _revoker, _pauser);
    }

    [Fact]
    public async Task Quarantine_sets_an_active_record_with_its_creator()
    {
        var subject = new QuarantineSubject(QuarantineSubjectKind.GcMessage, "msg-1");

        var record = await _service.QuarantineAsync(subject, QuarantineReason.AnomalyRule, _system, TestContext.Current.CancellationToken);

        Assert.Equal(QuarantineStatus.Quarantined, record.Status);
        Assert.Equal(subject, record.Subject);
        Assert.Equal(QuarantineReason.AnomalyRule, record.Reason);
        Assert.Equal(_system, record.CreatedBy);
        Assert.Null(record.ReleasedBy);
        Assert.Equal(record, await _store.GetAsync(record.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Quarantining_an_already_quarantined_subject_returns_the_same_record_without_side_effects()
    {
        var subject = new QuarantineSubject(QuarantineSubjectKind.Agent, "agent-a");

        var first = await _service.QuarantineAsync(subject, QuarantineReason.CanaryHit, _system, TestContext.Current.CancellationToken);
        var second = await _service.QuarantineAsync(subject, QuarantineReason.OwnerAction, _owner, TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_revoker.Revoked);
        Assert.Single(_pauser.Paused);
    }

    [Fact]
    public async Task Quarantining_an_agent_revokes_its_tokens_and_pauses_its_vms_exactly_once()
    {
        var subject = new QuarantineSubject(QuarantineSubjectKind.Agent, "agent-a");

        await _service.QuarantineAsync(subject, QuarantineReason.CanaryHit, _system, TestContext.Current.CancellationToken);

        Assert.Equal(["agent-a"], _revoker.Revoked);
        Assert.Equal(["agent-a"], _pauser.Paused);
    }

    [Theory]
    [InlineData(QuarantineSubjectKind.GcMessage)]
    [InlineData(QuarantineSubjectKind.MemoryNote)]
    [InlineData(QuarantineSubjectKind.VaultFile)]
    [InlineData(QuarantineSubjectKind.Handover)]
    public async Task Quarantining_non_agent_content_leaves_agent_controls_alone(QuarantineSubjectKind kind)
    {
        await _service.QuarantineAsync(new QuarantineSubject(kind, "x"), QuarantineReason.ScamMail, _system, TestContext.Current.CancellationToken);

        Assert.Empty(_revoker.Revoked);
        Assert.Empty(_pauser.Paused);
    }

    [Fact]
    public async Task An_agent_cannot_release_a_quarantine()
    {
        var record = await _service.QuarantineAsync(new QuarantineSubject(QuarantineSubjectKind.GcMessage, "msg-1"), QuarantineReason.AnomalyRule, _system, TestContext.Current.CancellationToken);

        var decision = await _service.ReleaseAsync(record.Id, _agent, TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Reason);
        Assert.Equal(QuarantineStatus.Quarantined, (await _store.GetAsync(record.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task A_reader_cannot_release_a_quarantine()
    {
        var record = await _service.QuarantineAsync(new QuarantineSubject(QuarantineSubjectKind.Handover, "h-1"), QuarantineReason.AnomalyRule, _system, TestContext.Current.CancellationToken);

        var decision = await _service.ReleaseAsync(record.Id, _reader, TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.Equal(QuarantineStatus.Quarantined, (await _store.GetAsync(record.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task The_owner_can_release_and_the_release_records_released_by()
    {
        var record = await _service.QuarantineAsync(new QuarantineSubject(QuarantineSubjectKind.MemoryNote, "agents/a/note.md"), QuarantineReason.OwnerAction, _owner, TestContext.Current.CancellationToken);

        var decision = await _service.ReleaseAsync(record.Id, _owner, TestContext.Current.CancellationToken);

        Assert.True(decision.Allowed);
        Assert.Null(decision.Reason);
        Assert.NotNull(decision.Record);
        Assert.Equal(QuarantineStatus.Released, decision.Record.Status);
        Assert.Equal(_owner, decision.Record.ReleasedBy);
        Assert.Equal(_owner, (await _store.GetAsync(record.Id, TestContext.Current.CancellationToken))!.ReleasedBy);
        Assert.Equal(record.CreatedBy, decision.Record.CreatedBy);
        Assert.Equal(record.Subject, decision.Record.Subject);
    }

    [Fact]
    public async Task Releasing_twice_is_refused_and_keeps_the_first_releaser()
    {
        var record = await _service.QuarantineAsync(new QuarantineSubject(QuarantineSubjectKind.GcMessage, "msg-2"), QuarantineReason.AnomalyRule, _system, TestContext.Current.CancellationToken);
        await _service.ReleaseAsync(record.Id, _owner, TestContext.Current.CancellationToken);

        var second = await _service.ReleaseAsync(record.Id, new QuarantineActor("owner-2", QuarantineActorKind.Owner), TestContext.Current.CancellationToken);

        Assert.False(second.Allowed);
        Assert.Equal(_owner, (await _store.GetAsync(record.Id, TestContext.Current.CancellationToken))!.ReleasedBy);
    }

    [Fact]
    public async Task Releasing_an_unknown_record_is_refused()
    {
        var decision = await _service.ReleaseAsync(Guid.CreateVersion7(), _owner, TestContext.Current.CancellationToken);

        Assert.False(decision.Allowed);
        Assert.Null(decision.Record);
    }

    [Fact]
    public async Task Release_does_not_restore_agent_tokens_or_unpause_vms()
    {
        var record = await _service.QuarantineAsync(new QuarantineSubject(QuarantineSubjectKind.Agent, "agent-a"), QuarantineReason.CanaryHit, _system, TestContext.Current.CancellationToken);

        await _service.ReleaseAsync(record.Id, _owner, TestContext.Current.CancellationToken);

        Assert.Single(_revoker.Revoked);
        Assert.Single(_pauser.Paused);
    }

    [Fact]
    public async Task A_released_subject_can_be_quarantined_again_with_a_new_record()
    {
        var subject = new QuarantineSubject(QuarantineSubjectKind.GcMessage, "msg-3");
        var first = await _service.QuarantineAsync(subject, QuarantineReason.AnomalyRule, _system, TestContext.Current.CancellationToken);
        await _service.ReleaseAsync(first.Id, _owner, TestContext.Current.CancellationToken);

        var second = await _service.QuarantineAsync(subject, QuarantineReason.OwnerAction, _owner, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(QuarantineStatus.Quarantined, second.Status);
        Assert.Equal(QuarantineStatus.Released, (await _store.GetAsync(first.Id, TestContext.Current.CancellationToken))!.Status);
    }
}
