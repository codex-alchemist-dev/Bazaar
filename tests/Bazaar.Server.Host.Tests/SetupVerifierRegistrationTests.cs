using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

/// <summary>The setup component registers the verifier, and a module's own check replaces its placeholder (doc 45 section 1.12).</summary>
public sealed class SetupVerifierRegistrationTests
{
    private sealed class FixedCheck(string id) : ISetupVerificationCheck
    {
        public string Id { get; } = id;

        public string Title => "fixed";

        public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken) =>
            Task.FromResult(SetupCheckResult.Green("ok"));
    }

    private static ServiceProvider Build(params ISetupVerificationCheck[] checks)
    {
        var services = new ServiceCollection();
        new SetupComponent().AddServices(services, new ConfigurationBuilder().Build());
        foreach (var check in checks)
            services.AddSingleton(check);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Verifier_is_registered_and_lists_placeholders_as_not_evaluated()
    {
        using var provider = Build();

        var report = await provider.GetRequiredService<ISetupVerifier>()
            .VerifyAsync(new SetupVerificationContext(new SetupPlan()), TestContext.Current.CancellationToken);

        Assert.Contains(report.Checks, c => c.Id == "server.identity" && c.Status == SetupCheckStatus.NotEvaluated);
    }

    [Fact]
    public async Task A_registered_check_replaces_the_placeholder_with_its_id()
    {
        using var provider = Build(new FixedCheck("server.identity"));

        var report = await provider.GetRequiredService<ISetupVerifier>()
            .VerifyAsync(new SetupVerificationContext(new SetupPlan()), TestContext.Current.CancellationToken);

        Assert.Contains(report.Checks, c => c.Id == "server.identity" && c.Status == SetupCheckStatus.Green);
    }
}
