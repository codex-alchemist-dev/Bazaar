namespace Bazaar.Server.TalentPool.Tests;

public sealed class TalentPoolServiceTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public InMemoryTalentTemplateStore Store { get; } = new();
        public FakeWhitelist Whitelist { get; } = new("haiku-5-5", "sonnet-5-5");
        public FakeHireGate Gate { get; } = new();
        public TalentPoolService Pool { get; }

        public Rig(ITalentHireGate? gate = null) => Pool = new TalentPoolService(Store, Whitelist, gate ?? Gate);
    }

    private static TalentInstantiateRequest Request(Guid id, string[]? tools = null, string[]? models = null, int version = 1) =>
        new(id, version, Samples.Actor(), tools ?? [], models ?? ["haiku-5-5"]);

    [Fact]
    public async Task A_submission_is_quarantined_not_hired()
    {
        var rig = new Rig();

        var result = await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct);

        Assert.Equal(TemplateSubmitOutcome.Quarantined, result.Outcome);
        Assert.Equal(1, result.Version);
        var stored = await rig.Store.GetAsync(result.TemplateId!.Value, 1, _ct);
        Assert.Equal(TalentTemplateStatus.Quarantined, stored!.Status);
        Assert.Equal(Samples.Actor().SoulId, stored.SourceSoulId);
    }

    [Fact]
    public async Task A_refused_manifest_is_not_stored()
    {
        var rig = new Rig();

        var result = await rig.Pool.SubmitAsync(Samples.Manifest(models: ["gpt-mystery"]), Samples.Actor(), _ct);

        Assert.Equal(TemplateSubmitOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Errors, e => e.Code == TalentManifestErrorCodes.ModelNotWhitelisted);
        Assert.Null(result.TemplateId);
    }

    [Fact]
    public async Task A_memory_file_is_rejected_at_submission()
    {
        var rig = new Rig();

        var result = await rig.Pool.SubmitAsync(Samples.Manifest() with { Files = ["memory/log.md"] }, Samples.Actor(), _ct);

        Assert.Equal(TemplateSubmitOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Errors, e => e.Code == TalentManifestErrorCodes.MemoryNotAllowed);
    }

    [Fact]
    public async Task Resubmitting_the_same_content_is_a_duplicate_and_new_content_is_the_next_version()
    {
        var rig = new Rig();
        var first = await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct);
        var id = first.TemplateId;

        var same = await rig.Pool.SubmitAsync(Samples.Manifest(templateId: id), Samples.Actor(), _ct);
        var next = await rig.Pool.SubmitAsync(Samples.Manifest(prompt: "Better.", templateId: id), Samples.Actor(), _ct);

        Assert.Equal(TemplateSubmitOutcome.Duplicate, same.Outcome);
        Assert.Equal(1, same.Version);
        Assert.Equal(TemplateSubmitOutcome.Quarantined, next.Outcome);
        Assert.Equal(2, next.Version);
    }

    [Fact]
    public async Task A_version_for_an_unknown_template_or_from_another_soul_is_refused()
    {
        var rig = new Rig();
        var first = await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct);

        var unknown = await rig.Pool.SubmitAsync(Samples.Manifest(templateId: Guid.NewGuid()), Samples.Actor(), _ct);
        var stranger = await rig.Pool.SubmitAsync(Samples.Manifest(prompt: "Hijack.", templateId: first.TemplateId), Samples.Actor(Guid.NewGuid()), _ct);

        Assert.Equal(TemplateSubmitOutcome.Rejected, unknown.Outcome);
        Assert.Equal("unknown_template", unknown.Errors[0].Code);
        Assert.Equal(TemplateSubmitOutcome.Rejected, stranger.Outcome);
        Assert.Equal("not_template_source", stranger.Errors[0].Code);
    }

    [Fact]
    public async Task A_blank_actor_is_refused()
    {
        var rig = new Rig();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Pool.SubmitAsync(Samples.Manifest(), new TalentActor(" "), _ct));
    }

    [Fact]
    public async Task The_default_hire_gate_denies_so_nothing_is_instantiable()
    {
        var rig = new Rig(new DenyAllTalentHireGate());
        var id = (await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct)).TemplateId!.Value;

        var status = await rig.Pool.ConfirmHireAsync(id, 1, _ct);
        var decision = await rig.Pool.EvaluateInstantiationAsync(Request(id), _ct);

        Assert.Equal(TalentTemplateStatus.Quarantined, status);
        Assert.False(decision.Allowed);
        Assert.Equal(InstantiationRefusal.NotHired, decision.Refusal);
    }

    [Fact]
    public async Task A_quarantined_template_is_not_instantiable_even_if_the_gate_would_pass()
    {
        var rig = new Rig();
        rig.Gate.Passed = true;
        var id = (await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct)).TemplateId!.Value;

        // The gate says yes, but nobody has confirmed the hire, so the stored status is still quarantined.
        var decision = await rig.Pool.EvaluateInstantiationAsync(Request(id), _ct);

        Assert.False(decision.Allowed);
        Assert.Equal(InstantiationRefusal.NotHired, decision.Refusal);
    }

    [Fact]
    public async Task A_passed_hire_motion_makes_the_template_instantiable()
    {
        var rig = new Rig();
        var id = (await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct)).TemplateId!.Value;
        rig.Gate.Passed = true;

        var status = await rig.Pool.ConfirmHireAsync(id, 1, _ct);
        var decision = await rig.Pool.EvaluateInstantiationAsync(Request(id), _ct);

        Assert.Equal(TalentTemplateStatus.Hired, status);
        Assert.True(decision.Allowed);
        Assert.False(decision.RequiresTier2);
    }

    [Fact]
    public async Task A_hired_template_is_refused_again_if_the_gate_stops_vouching_for_it()
    {
        var rig = new Rig();
        var id = (await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct)).TemplateId!.Value;
        rig.Gate.Passed = true;
        await rig.Pool.ConfirmHireAsync(id, 1, _ct);
        rig.Gate.Passed = false;

        var decision = await rig.Pool.EvaluateInstantiationAsync(Request(id), _ct);

        Assert.False(decision.Allowed);
        Assert.Equal(InstantiationRefusal.HireNotApproved, decision.Refusal);
    }

    [Fact]
    public async Task Confirming_an_unknown_version_returns_null()
    {
        Assert.Null(await new Rig().Pool.ConfirmHireAsync(Guid.NewGuid(), 1, _ct));
    }

    [Fact]
    public async Task Instantiating_with_any_tool_grant_is_flagged_tier_2_and_without_one_is_not()
    {
        var rig = new Rig();
        var id = (await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct)).TemplateId!.Value;
        rig.Gate.Passed = true;
        await rig.Pool.ConfirmHireAsync(id, 1, _ct);

        var granted = await rig.Pool.EvaluateInstantiationAsync(Request(id, tools: ["read_file"]), _ct);
        var none = await rig.Pool.EvaluateInstantiationAsync(Request(id), _ct);

        Assert.True(granted.Allowed);
        Assert.True(granted.RequiresTier2);
        Assert.True(Request(id, tools: ["x"]).RequiresTier2);
        Assert.False(none.RequiresTier2);
    }

    [Fact]
    public async Task A_tool_outside_the_templates_allowlist_is_refused_and_still_flagged_tier_2()
    {
        var rig = new Rig();
        var id = (await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct)).TemplateId!.Value;
        rig.Gate.Passed = true;
        await rig.Pool.ConfirmHireAsync(id, 1, _ct);

        var decision = await rig.Pool.EvaluateInstantiationAsync(Request(id, tools: ["delete_everything"]), _ct);

        Assert.False(decision.Allowed);
        Assert.Equal(InstantiationRefusal.ToolNotAllowed, decision.Refusal);
        Assert.True(decision.RequiresTier2);
    }

    [Fact]
    public async Task A_model_that_left_the_whitelist_after_submission_is_refused_at_instantiation()
    {
        var rig = new Rig();
        var id = (await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct)).TemplateId!.Value;
        rig.Gate.Passed = true;
        await rig.Pool.ConfirmHireAsync(id, 1, _ct);
        rig.Whitelist.Remove("haiku-5-5");

        var decision = await rig.Pool.EvaluateInstantiationAsync(Request(id), _ct);

        Assert.False(decision.Allowed);
        Assert.Equal(InstantiationRefusal.ModelNotAllowed, decision.Refusal);
    }

    [Fact]
    public async Task A_model_the_template_never_listed_is_refused_at_instantiation()
    {
        var rig = new Rig();
        var id = (await rig.Pool.SubmitAsync(Samples.Manifest(), Samples.Actor(), _ct)).TemplateId!.Value;
        rig.Gate.Passed = true;
        await rig.Pool.ConfirmHireAsync(id, 1, _ct);

        var decision = await rig.Pool.EvaluateInstantiationAsync(Request(id, models: ["sonnet-5-5"]), _ct);

        Assert.Equal(InstantiationRefusal.ModelNotAllowed, decision.Refusal);
    }

    [Fact]
    public async Task An_unknown_template_is_refused()
    {
        var decision = await new Rig().Pool.EvaluateInstantiationAsync(Request(Guid.NewGuid()), _ct);

        Assert.Equal(InstantiationRefusal.UnknownTemplate, decision.Refusal);
    }

    [Fact]
    public async Task The_deny_all_gate_never_passes()
    {
        Assert.False(await new DenyAllTalentHireGate().HasHirePassedAsync(Guid.NewGuid(), 1, new string('a', 64), _ct));
    }
}
