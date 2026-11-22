using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Constitution;
using Bazaar.Server.Setup.FirstRun;
using Bazaar.Server.Setup.FirstRun.Postgres;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

/// <summary>Which apply steps the setup component registers (doc 45 section 1.3). No database is opened: the data source is never used.</summary>
public sealed class SetupComponentStepTests
{
    private static ServiceProvider Build(SetupComponent component, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none"));
        services.AddSingleton(Bazaar.Server.Settings.BuiltInSettingRegistries.WithContributions([]));
        services.AddSingleton<IAuditLog>(new NullAudit());
        component.AddServices(services, new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_default_component_registers_the_install_settings_constitution_and_first_run_seed_steps()
    {
        using var provider = Build(new SetupComponent());

        var steps = provider.GetServices<ISetupStep>().OrderBy(step => step.Order).ToList();

        // The Constitution is drafted just before the first-run seed, which needs a commit to accept.
        Assert.Equal(["constitution.seed", "firstrun.seed", "install.settings"], steps.Select(step => step.Id));
        Assert.IsType<ConstitutionSeedStep>(steps[0]);
        Assert.IsType<FirstRunSeedStep>(steps[1]);
        Assert.IsType<InstallSettingsStep>(steps[2]);
    }

    [Fact]
    public void The_constitution_vault_is_unconfigured_without_the_memory_vault_settings()
    {
        using var provider = Build(new SetupComponent());

        Assert.IsType<UnconfiguredConstitutionVault>(provider.GetRequiredService<IConstitutionVault>());
    }

    [Fact]
    public void The_constitution_vault_is_the_git_vault_when_the_memory_vault_settings_are_set()
    {
        using var provider = Build(new SetupComponent(), new Dictionary<string, string?>
        {
            [ConstitutionVaultOptions.RepoPathKey] = Path.Combine(Path.GetTempPath(), "bazaar-unused-vault"),
            [ConstitutionVaultOptions.VaultIdKey] = Guid.NewGuid().ToString(),
            [ConstitutionVaultOptions.WriterNameKey] = "Writer",
            [ConstitutionVaultOptions.WriterEmailKey] = "writer@example.invalid",
        });

        Assert.IsType<GitConstitutionVault>(provider.GetRequiredService<IConstitutionVault>());
    }

    [Fact]
    public void The_first_run_seed_step_resolves_with_its_postgres_stores()
    {
        using var provider = Build(new SetupComponent());

        Assert.IsType<PostgresFirstRunStore>(provider.GetRequiredService<IFirstRunStore>());
        Assert.IsType<PostgresFirstRunOwner>(provider.GetRequiredService<IFirstRunOwner>());
        Assert.IsType<PostgresConstitutionAcceptance>(provider.GetRequiredService<IConstitutionAcceptance>());
        Assert.IsType<PostgresDefaultAgentRoutes>(provider.GetRequiredService<IDefaultAgentRoutes>());
        Assert.IsType<AuditLogFirstRunAudit>(provider.GetRequiredService<IFirstRunAudit>());
        Assert.NotNull(provider.GetRequiredService<FirstRunSeedStep>());
    }

    [Fact]
    public void The_first_run_section_is_covered_so_an_apply_is_not_held_for_it()
    {
        using var provider = Build(new SetupComponent());

        Assert.Contains(provider.GetServices<ISetupStep>(), step => step.Covers.Contains("firstRun"));
    }

    [Fact]
    public void Steps_passed_to_the_constructor_replace_the_defaults()
    {
        var own = new OwnStep();
        using var provider = Build(new SetupComponent([own]));

        Assert.Same(own, Assert.Single(provider.GetServices<ISetupStep>()));
    }

    [Fact]
    public async Task The_progress_list_reports_the_registered_step_as_pending()
    {
        using var provider = Build(new SetupComponent());

        var snapshot = await provider.GetRequiredService<Bazaar.Server.Setup.Http.ISetupProgressSource>().GetAsync(TestContext.Current.CancellationToken);

        Assert.Contains(snapshot.Steps, progress => progress.Id == "install.settings");
        Assert.Contains(snapshot.Steps, progress => progress.Id == "firstrun.seed");
        Assert.Contains(snapshot.Steps, progress => progress.Id == "constitution.seed");
    }

    private sealed class NullAudit : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class OwnStep : ISetupStep
    {
        public string Id => "own.step";

        public int Order => 1;

        public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) =>
            Task.FromResult(SetupStepResult.Ok);
    }
}
