namespace Bazaar.Server.Setup.FirstRun.Tests;

internal sealed class FakeWorld : IFirstRunOwner, IFirstRunStore, IConstitutionAcceptance, IDefaultAgentRoutes, IFirstRunAudit
{
    public Guid? Owner { get; set; } = Guid.NewGuid();
    public IReadOnlyList<string> Whitelist { get; set; } = ["claude-code-cli:claude-sonnet-5-5"];
    public bool HasConstitution { get; set; } = true;

    public Guid? GcId { get; private set; }
    public Guid? AgentId { get; private set; }
    public bool AgentActive { get; private set; }
    public IReadOnlyList<string> AgentWhitelist { get; private set; } = [];
    public Guid? AgentGc { get; private set; }
    public bool GcArchived { get; private set; }
    public int Acceptances { get; private set; }
    public List<(Guid? Gc, Guid? Agent, bool Accepted, string Operator)> Audits { get; } = [];

    public Task<Guid?> GetOwnerPrincipalIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(Owner);

    public Task<SeededGc> EnsureGeneralGcAsync(string title, Guid ownerPrincipalId, CancellationToken cancellationToken = default)
    {
        var created = GcId is null;
        GcId ??= Guid.NewGuid();
        return Task.FromResult(new SeededGc(GcId.Value, created));
    }

    public Task<SeededAgent> EnsureDefaultAgentAsync(string name, string purpose, Guid? gcId, IReadOnlyList<string> whitelist, CancellationToken cancellationToken = default)
    {
        var created = AgentId is null;
        AgentId ??= Guid.NewGuid();
        if (created)
        {
            AgentWhitelist = whitelist;
            AgentGc = gcId;
        }

        return Task.FromResult(new SeededAgent(AgentId.Value, created));
    }

    public Task ActivateAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        AgentActive = true;
        return Task.CompletedTask;
    }

    public Task RemoveAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        AgentId = null;
        AgentActive = false;
        return Task.CompletedTask;
    }

    public Task ArchiveGcAsync(Guid gcId, CancellationToken cancellationToken = default)
    {
        GcArchived = true;
        return Task.CompletedTask;
    }

    public Task<bool> RecordOwnerAcceptanceAsync(Guid ownerPrincipalId, CancellationToken cancellationToken = default)
    {
        if (!HasConstitution) return Task.FromResult(false);
        Acceptances++;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> GetWhitelistAsync(IReadOnlyList<string> requestedSources, string profile, CancellationToken cancellationToken = default)
    {
        RequestedSources = requestedSources;
        Profile = profile;
        return Task.FromResult(Whitelist);
    }

    public string Profile { get; private set; } = "";
    public IReadOnlyList<string> RequestedSources { get; private set; } = [];

    public Task WriteSeededAsync(Guid? gcId, Guid? agentId, bool constitutionAccepted, string operatorIdentity, CancellationToken cancellationToken = default)
    {
        Audits.Add((gcId, agentId, constitutionAccepted, operatorIdentity));
        return Task.CompletedTask;
    }
}

public class FirstRunSeedStepTests
{
    private const string _hash = "abc";

    private static SetupApplyContext Context(string hash = _hash) =>
        new(hash, SetupEntryPath.Api, "setup:bootstrap", null, null, SetupStateSnapshot.Empty, []);

    private static SetupPlan Plan(bool accept = false, bool gc = true, bool agent = true) =>
        new() { FirstRun = new SetupFirstRunSection { AcceptConstitution = accept, SeedGeneralGc = gc, SeedDefaultAgent = agent } };

    private static FirstRunSeedStep Step(FakeWorld world) => new(world, world, world, world, world);

    [Fact]
    public async Task Seeds_the_gc_and_a_provisioned_agent_that_waits_for_acceptance()
    {
        var world = new FakeWorld();

        var result = await Step(world).ApplyAsync(Plan(), Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(world.GcId);
        Assert.Equal(world.GcId, world.AgentGc);
        Assert.False(world.AgentActive);
        Assert.Equal(0, world.Acceptances);
        Assert.Contains("Constitution", result.Message);
        Assert.Equal(["claude-code-cli:claude-sonnet-5-5"], world.AgentWhitelist);
        Assert.False(Assert.Single(world.Audits).Accepted);
    }

    [Fact]
    public async Task The_owners_acceptance_activates_the_agent()
    {
        var world = new FakeWorld();

        var result = await Step(world).ApplyAsync(Plan(accept: true), Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, world.Acceptances);
        Assert.True(world.AgentActive);
        Assert.True(Assert.Single(world.Audits).Accepted);
    }

    [Fact]
    public async Task Acceptance_with_no_constitution_commit_leaves_the_agent_inactive()
    {
        var world = new FakeWorld { HasConstitution = false };

        await Step(world).ApplyAsync(Plan(accept: true), Context(), CancellationToken.None);

        Assert.False(world.AgentActive);
        Assert.False(Assert.Single(world.Audits).Accepted);
    }

    [Fact]
    public async Task Applying_twice_changes_nothing_the_second_time()
    {
        var world = new FakeWorld();
        var step = Step(world);
        await step.ApplyAsync(Plan(accept: true), Context(), CancellationToken.None);
        var gc = world.GcId;
        var agent = world.AgentId;

        var second = await step.ApplyAsync(Plan(accept: true), Context("again"), CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal(gc, world.GcId);
        Assert.Equal(agent, world.AgentId);
    }

    [Fact]
    public async Task A_missing_provider_still_creates_the_agent_with_no_route_and_says_so()
    {
        var world = new FakeWorld { Whitelist = [] };

        var result = await Step(world).ApplyAsync(Plan(accept: true), Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(world.AgentWhitelist);
        Assert.Contains("No Claude model source", result.Message);
    }

    [Fact]
    public async Task The_requested_sources_reach_the_route_port_and_the_default_name_is_Claude()
    {
        var world = new FakeWorld();
        var plan = new SetupPlan { FirstRun = new SetupFirstRunSection { DefaultAgentSources = ["anthropic-api", "freellmapi"] } };

        await Step(world).ApplyAsync(plan, Context(), CancellationToken.None);

        Assert.Equal(["anthropic-api", "freellmapi"], world.RequestedSources);
        Assert.Equal("Claude", new SetupFirstRunSection().DefaultAgentName);
        Assert.Equal("balanced", world.Profile);
    }

    [Fact]
    public async Task No_owner_fails_the_step_and_creates_nothing()
    {
        var world = new FakeWorld { Owner = null };

        var result = await Step(world).ApplyAsync(Plan(), Context(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(world.GcId);
        Assert.Null(world.AgentId);
    }

    [Fact]
    public async Task Both_seeds_off_does_nothing()
    {
        var world = new FakeWorld { Owner = null };

        var result = await Step(world).ApplyAsync(Plan(gc: false, agent: false), Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(world.Audits);
    }

    [Fact]
    public async Task The_agent_alone_has_no_gc()
    {
        var world = new FakeWorld();

        await Step(world).ApplyAsync(Plan(gc: false), Context(), CancellationToken.None);

        Assert.Null(world.GcId);
        Assert.Null(world.AgentGc);
        Assert.NotNull(world.AgentId);
    }

    [Fact]
    public async Task Rollback_removes_only_what_this_run_created()
    {
        var world = new FakeWorld();
        var step = Step(world);
        await step.ApplyAsync(Plan(), Context("first"), CancellationToken.None);
        await step.ApplyAsync(Plan(), Context("second"), CancellationToken.None);

        await step.RollbackAsync(Plan(), Context("second"), CancellationToken.None);
        Assert.NotNull(world.AgentId);
        Assert.False(world.GcArchived);

        await step.RollbackAsync(Plan(), Context("first"), CancellationToken.None);
        Assert.Null(world.AgentId);
        Assert.True(world.GcArchived);
    }

    [Fact]
    public async Task Messages_and_results_carry_no_names_or_ids()
    {
        var world = new FakeWorld();
        var plan = new SetupPlan { FirstRun = new SetupFirstRunSection { DefaultAgentName = "CanaryAgentName", GeneralGcTitle = "CanaryTitle" } };

        var result = await Step(world).ApplyAsync(plan, Context(), CancellationToken.None);

        Assert.DoesNotContain("Canary", result.Message ?? "");
        Assert.DoesNotContain(world.GcId!.Value.ToString(), result.Message ?? "");
    }

    [Fact]
    public void The_step_runs_after_the_owner_and_company_source_steps()
    {
        Assert.True(FirstRunSeedStep.StepOrder > 130);
        Assert.Equal("firstrun.seed", FirstRunSeedStep.StepId);
    }
}

public class FirstRunPlanSectionTests
{
    private const string _base = """
        {"schemaVersion":1,"install":{"companyName":"X","companySlug":"x"},"custody":{"kekProvider":"dpapi","kekRef":"dpapi:k"},
         "owner":{"displayName":"D"},"backups":{"acknowledgeNoTarget":true},"company":{"source":"blank"}
         FIRSTRUN}
        """;

    private static SetupLoadResult Load(string firstRun) => SetupPlanLoader.Load(_base.Replace("FIRSTRUN", firstRun), Bazaar.Server.Setup.Host.RegistrySetupSettingCatalog.Known);

    [Fact]
    public void The_section_may_be_left_out_and_its_defaults_apply()
    {
        var loaded = Load("");

        Assert.Equal(SetupExitCode.Ok, loaded.ExitCode);
        Assert.True(loaded.Plan!.FirstRun.SeedGeneralGc);
        Assert.True(loaded.Plan.FirstRun.SeedDefaultAgent);
        Assert.False(loaded.Plan.FirstRun.AcceptConstitution);
        Assert.Equal("General", loaded.Plan.FirstRun.GeneralGcTitle);
    }

    [Fact]
    public void Acceptance_is_read_and_changes_the_plan_hash()
    {
        var off = Load("").PlanHash;
        var on = Load(",\"firstRun\":{\"acceptConstitution\":true}");

        Assert.True(on.Plan!.FirstRun.AcceptConstitution);
        Assert.NotEqual(off, on.PlanHash);
    }

    [Fact]
    public void Unknown_keys_and_blank_names_are_rejected()
    {
        Assert.Equal(SetupExitCode.InvalidInput, Load(",\"firstRun\":{\"nope\":1}").ExitCode);
        Assert.Contains(Load(",\"firstRun\":{\"defaultAgentName\":\" \"}").Issues, i => i.Path == "firstRun.defaultAgentName");
        Assert.Contains(Load(",\"firstRun\":{\"defaultAgentSources\":[\"A b\"]}").Issues, i => i.Path == "firstRun.defaultAgentSources[0]");
        Assert.Contains(Load(",\"firstRun\":{\"defaultAgentSources\":[\"x\",\"x\"]}").Issues, i => i.Path == "firstRun.defaultAgentSources[1]");
    }
}
