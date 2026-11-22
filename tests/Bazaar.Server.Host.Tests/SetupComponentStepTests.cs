using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.Setup.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Host.Tests;

/// <summary>Which apply steps the setup component registers (doc 45 section 1.3). No database is opened: the data source is never used.</summary>
public sealed class SetupComponentStepTests
{
    private static ServiceProvider Build(SetupComponent component)
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none"));
        services.AddSingleton(Bazaar.Server.Settings.BuiltInSettingRegistries.Known);
        component.AddServices(services, new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_default_component_registers_the_install_settings_step()
    {
        using var provider = Build(new SetupComponent());

        var step = Assert.Single(provider.GetServices<ISetupStep>());

        Assert.IsType<InstallSettingsStep>(step);
        Assert.Equal("install.settings", step.Id);
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

        var progress = Assert.Single(snapshot.Steps);
        Assert.Equal("install.settings", progress.Id);
    }

    private sealed class OwnStep : ISetupStep
    {
        public string Id => "own.step";

        public int Order => 1;

        public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) =>
            Task.FromResult(SetupStepResult.Ok);
    }
}
