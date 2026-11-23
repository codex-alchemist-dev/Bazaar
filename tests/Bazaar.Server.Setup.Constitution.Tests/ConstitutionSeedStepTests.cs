namespace Bazaar.Server.Setup.Constitution.Tests;

internal sealed class FakeVault : IConstitutionVault
{
    public int Calls { get; private set; }
    public string? Markdown { get; private set; }
    public bool Exists { get; set; }
    public Exception? Failure { get; set; }

    public Task<ConstitutionSeedResult> EnsureAsync(string markdown, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Failure is not null) throw Failure;
        Markdown = markdown;
        var created = !Exists;
        Exists = true;
        return Task.FromResult(new ConstitutionSeedResult(new string('a', 40), created));
    }
}

public class ConstitutionSeedStepTests
{
    private static SetupApplyContext Context() =>
        new("abc", SetupEntryPath.Api, "setup:bootstrap", null, null, SetupStateSnapshot.Empty, []);

    private static SetupPlan Plan(bool agent = true, bool accept = false) =>
        new() { FirstRun = new SetupFirstRunSection { SeedDefaultAgent = agent, AcceptConstitution = accept } };

    [Fact]
    public void It_runs_before_the_first_run_seed_and_covers_the_first_run_section()
    {
        var step = new ConstitutionSeedStep(new FakeVault());

        Assert.True(step.Order < 135);
        Assert.Contains("firstRun", step.Covers);
        Assert.True(SetupPlanSections.IsKnown("firstRun"));
    }

    [Fact]
    public async Task It_seeds_the_default_constitution_and_never_accepts_it()
    {
        var vault = new FakeVault();

        var result = await new ConstitutionSeedStep(vault).ApplyAsync(Plan(), Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("Constitution", result.Message);
        Assert.Contains("type: constitution", vault.Markdown);
    }

    [Fact]
    public async Task A_second_apply_succeeds_with_no_new_message()
    {
        var vault = new FakeVault();
        var step = new ConstitutionSeedStep(vault);
        await step.ApplyAsync(Plan(), Context(), CancellationToken.None);

        var second = await step.ApplyAsync(Plan(), Context(), CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Null(second.Message);
    }

    [Fact]
    public async Task It_does_nothing_when_no_agent_is_seeded_and_nothing_is_accepted()
    {
        var vault = new FakeVault();

        var result = await new ConstitutionSeedStep(vault).ApplyAsync(Plan(agent: false), Context(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, vault.Calls);
    }

    [Fact]
    public async Task Acceptance_alone_still_needs_the_constitution()
    {
        var vault = new FakeVault();

        await new ConstitutionSeedStep(vault).ApplyAsync(Plan(agent: false, accept: true), Context(), CancellationToken.None);

        Assert.Equal(1, vault.Calls);
    }

    [Fact]
    public async Task A_vault_failure_fails_the_step_with_the_message()
    {
        var vault = new FakeVault { Failure = new ConstitutionVaultException("The Vault is not configured.") };

        var result = await new ConstitutionSeedStep(vault).ApplyAsync(Plan(), Context(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("The Vault is not configured.", result.Message);
    }

    [Fact]
    public async Task The_unconfigured_vault_names_the_missing_settings()
    {
        var result = await new ConstitutionSeedStep(new UnconfiguredConstitutionVault()).ApplyAsync(Plan(), Context(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(ConstitutionVaultOptions.RepoPathKey, result.Message);
        Assert.Contains(ConstitutionVaultOptions.VaultIdKey, result.Message);
    }

    [Fact]
    public async Task Rollback_leaves_the_vault_alone()
    {
        var vault = new FakeVault();
        var step = new ConstitutionSeedStep(vault);

        await step.RollbackAsync(Plan(), Context(), CancellationToken.None);

        Assert.Equal(0, vault.Calls);
    }

    [Fact]
    public void Options_need_all_four_settings()
    {
        var all = new Dictionary<string, string?>
        {
            [ConstitutionVaultOptions.RepoPathKey] = "/v",
            [ConstitutionVaultOptions.VaultIdKey] = Guid.NewGuid().ToString(),
            [ConstitutionVaultOptions.WriterNameKey] = "Writer",
            [ConstitutionVaultOptions.WriterEmailKey] = "w@example.invalid",
        };

        Assert.NotNull(ConstitutionVaultOptions.FromSettings(key => all.GetValueOrDefault(key)));
        foreach (var missing in all.Keys)
        {
            var partial = all.Where(pair => pair.Key != missing).ToDictionary();
            Assert.Null(ConstitutionVaultOptions.FromSettings(key => partial.GetValueOrDefault(key)));
        }

        all[ConstitutionVaultOptions.VaultIdKey] = "not-a-guid";
        Assert.Null(ConstitutionVaultOptions.FromSettings(key => all.GetValueOrDefault(key)));
    }
}
