using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.TalentPool.Tests;

internal sealed class InMemoryHireOutcomeStore : ITalentHireOutcomeStore
{
    private readonly Dictionary<Guid, HireMotionRecord> _rows = [];

    public Task<bool> AddMotionAsync(HireMotionRecord motion, CancellationToken ct) => Task.FromResult(_rows.TryAdd(motion.MotionId, motion));

    public Task<HireMotionRecord?> GetMotionAsync(Guid motionId, CancellationToken ct) =>
        Task.FromResult(_rows.GetValueOrDefault(motionId));

    public Task<IReadOnlyList<HireMotionRecord>> ListMotionsAsync(Guid templateId, int version, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HireMotionRecord>>([.. _rows.Values.Where(m => m.TemplateId == templateId && m.Version == version)]);

    public Task SetResultAsync(Guid motionId, CouncilHireResult result, CancellationToken ct)
    {
        _rows[motionId] = _rows[motionId] with { Result = result };
        return Task.CompletedTask;
    }
}

internal sealed class FakeCouncilMotions : ICouncilHireMotions
{
    public List<HireMotionRequest> Requests { get; } = [];

    public Task<Guid> SubmitHireMotionAsync(HireMotionRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Guid.CreateVersion7());
    }
}

internal sealed class RecordingAudit : ITalentAuditSink
{
    public List<TalentAuditEvent> Events { get; } = [];

    public Task RecordAsync(TalentAuditEvent auditEvent, CancellationToken ct)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}

public sealed class CouncilHireTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public InMemoryTalentTemplateStore Templates { get; } = new();
        public InMemoryHireOutcomeStore Outcomes { get; } = new();
        public FakeCouncilMotions Motions { get; } = new();
        public RecordingAudit Audit { get; } = new();
        public TalentHireOutcomeHandler Handler { get; }
        public CouncilHireGate Gate { get; }
        public TalentPoolService Pool { get; }

        public Rig()
        {
            Handler = new TalentHireOutcomeHandler(Templates, Motions, Outcomes, Audit);
            Gate = new CouncilHireGate(Outcomes);
            Pool = new TalentPoolService(Templates, new FakeWhitelist("haiku-5-5"), Gate);
        }

        public async Task<(Guid Template, Guid Motion, string Sha)> SubmitAsync()
        {
            var added = await Templates.AddVersionAsync(Guid.CreateVersion7(), Samples.Validated(), null, "agent:alice", _ct);
            var motion = await Handler.SubmitHireMotionAsync(added.Template.TemplateId, 1, "agent:alice", _ct);
            return (added.Template.TemplateId, motion!.Value, added.Template.ContentSha);
        }

        public Task<bool> OutcomeAsync(Guid motion, Guid template, CouncilHireResult result, int version = 1) =>
            Handler.HandleAsync(new CouncilHireOutcome(motion, template, version, result), _ct);
    }

    [Theory]
    [InlineData(CouncilHireResult.Passed, true)]
    [InlineData(CouncilHireResult.OwnerApproved, true)]
    [InlineData(CouncilHireResult.Failed, false)]
    [InlineData(CouncilHireResult.EscalatedToOwner, false)]
    [InlineData(CouncilHireResult.OwnerRejected, false)]
    public async Task Only_passed_or_owner_approved_opens_the_gate(CouncilHireResult result, bool approved)
    {
        var rig = new Rig();
        var (template, motion, sha) = await rig.SubmitAsync();
        Assert.False(await rig.Gate.HasHirePassedAsync(template, 1, sha, _ct));

        await rig.OutcomeAsync(motion, template, result);

        Assert.Equal(approved, await rig.Gate.HasHirePassedAsync(template, 1, sha, _ct));
    }

    [Fact]
    public async Task Submit_sends_the_content_hash_and_proposer()
    {
        var rig = new Rig();
        var (template, _, sha) = await rig.SubmitAsync();

        var request = Assert.Single(rig.Motions.Requests);
        Assert.Equal(new HireMotionRequest(template, 1, sha, "agent:alice"), request);
    }

    [Fact]
    public async Task Submit_refuses_a_version_that_is_not_quarantined()
    {
        var rig = new Rig();
        var (template, _, _) = await rig.SubmitAsync();
        await rig.Templates.TryResolveAsync(template, 1, TalentTemplateStatus.Hired, _ct);

        Assert.Null(await rig.Handler.SubmitHireMotionAsync(template, 1, "agent:alice", _ct));
        Assert.Null(await rig.Handler.SubmitHireMotionAsync(Guid.NewGuid(), 1, "agent:alice", _ct));
    }

    [Fact]
    public async Task Escalated_then_owner_approved_opens_the_gate()
    {
        var rig = new Rig();
        var (template, motion, sha) = await rig.SubmitAsync();

        Assert.True(await rig.OutcomeAsync(motion, template, CouncilHireResult.EscalatedToOwner));
        Assert.False(await rig.Gate.HasHirePassedAsync(template, 1, sha, _ct));
        Assert.True(await rig.OutcomeAsync(motion, template, CouncilHireResult.OwnerApproved));

        Assert.True(await rig.Gate.HasHirePassedAsync(template, 1, sha, _ct));
        Assert.Empty(rig.Audit.Events);
    }

    [Fact]
    public async Task Unknown_motion_is_ignored_and_audited()
    {
        var rig = new Rig();
        var (template, _, sha) = await rig.SubmitAsync();

        Assert.False(await rig.OutcomeAsync(Guid.NewGuid(), template, CouncilHireResult.Passed));

        Assert.False(await rig.Gate.HasHirePassedAsync(template, 1, sha, _ct));
        Assert.Equal("talent.hire_outcome_ignored", Assert.Single(rig.Audit.Events).Kind);
    }

    [Fact]
    public async Task Wrong_version_or_template_is_ignored_and_audited()
    {
        var rig = new Rig();
        var (template, motion, sha) = await rig.SubmitAsync();

        Assert.False(await rig.OutcomeAsync(motion, template, CouncilHireResult.Passed, version: 2));
        Assert.False(await rig.OutcomeAsync(motion, Guid.NewGuid(), CouncilHireResult.Passed));

        Assert.False(await rig.Gate.HasHirePassedAsync(template, 1, sha, _ct));
        Assert.Equal(2, rig.Audit.Events.Count);
    }

    [Fact]
    public async Task Replay_is_idempotent_and_not_audited()
    {
        var rig = new Rig();
        var (template, motion, sha) = await rig.SubmitAsync();

        Assert.True(await rig.OutcomeAsync(motion, template, CouncilHireResult.Passed));
        Assert.False(await rig.OutcomeAsync(motion, template, CouncilHireResult.Passed));

        Assert.True(await rig.Gate.HasHirePassedAsync(template, 1, sha, _ct));
        Assert.Empty(rig.Audit.Events);
    }

    [Fact]
    public async Task A_conflicting_result_after_a_final_one_is_ignored_and_audited()
    {
        var rig = new Rig();
        var (template, motion, sha) = await rig.SubmitAsync();
        await rig.OutcomeAsync(motion, template, CouncilHireResult.Passed);

        Assert.False(await rig.OutcomeAsync(motion, template, CouncilHireResult.Failed));
        Assert.False(await rig.OutcomeAsync(motion, template, CouncilHireResult.EscalatedToOwner));

        Assert.True(await rig.Gate.HasHirePassedAsync(template, 1, sha, _ct));
        Assert.Equal(2, rig.Audit.Events.Count);
    }

    [Fact]
    public async Task The_gate_denies_when_the_content_hash_differs()
    {
        var rig = new Rig();
        var (template, motion, _) = await rig.SubmitAsync();
        await rig.OutcomeAsync(motion, template, CouncilHireResult.Passed);

        Assert.False(await rig.Gate.HasHirePassedAsync(template, 1, new string('0', 64), _ct));
    }

    [Fact]
    public async Task A_passed_outcome_lets_the_pool_confirm_the_hire()
    {
        var rig = new Rig();
        var (template, motion, _) = await rig.SubmitAsync();
        Assert.Equal(TalentTemplateStatus.Quarantined, await rig.Pool.ConfirmHireAsync(template, 1, _ct));

        await rig.OutcomeAsync(motion, template, CouncilHireResult.Passed);

        Assert.Equal(TalentTemplateStatus.Hired, await rig.Pool.ConfirmHireAsync(template, 1, _ct));
    }

    [Fact]
    public void Registration_stays_deny_all_until_a_motion_port_and_store_exist()
    {
        var bare = new ServiceCollection();
        bare.AddSingleton<ITalentModelWhitelist>(new FakeWhitelist());
        bare.AddSingleton<ITalentTemplateStore>(new InMemoryTalentTemplateStore());
        bare.AddBazaarTalentPool();
        Assert.IsType<DenyAllTalentHireGate>(bare.BuildServiceProvider().GetRequiredService<ITalentHireGate>());

        var wired = new ServiceCollection();
        wired.AddSingleton<ICouncilHireMotions>(new FakeCouncilMotions());
        wired.AddSingleton<ITalentHireOutcomeStore>(new InMemoryHireOutcomeStore());
        wired.AddBazaarTalentPool();
        Assert.IsType<CouncilHireGate>(wired.BuildServiceProvider().GetRequiredService<ITalentHireGate>());
    }
}
