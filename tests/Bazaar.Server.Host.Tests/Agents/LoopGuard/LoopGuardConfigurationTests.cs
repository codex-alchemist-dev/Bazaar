using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Server.Host.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Tests.Agents.LoopGuard;

public sealed class LoopGuardConfigurationTests
{
    [Fact]
    public void Host_configuration_reaches_the_loop_guard()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{LoopGuardOptions.SectionName}:PingPongLimit"] = "2",
                [$"{LoopGuardOptions.SectionName}:MentionDepth"] = "3",
                [$"{LoopGuardOptions.SectionName}:WakesPerHour"] = "5",
                [$"{LoopGuardOptions.SectionName}:ThreadAgentMessages"] = "7",
                [$"{LoopGuardOptions.SectionName}:RepeatToolCall"] = "2",
                [$"{LoopGuardOptions.SectionName}:ScoredWakesPerHour"] = "4",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddBazaarAgentSafety(configuration);

        using var provider = services.BuildServiceProvider();
        var guard = provider.GetRequiredService<ILoopGuard>();

        // WakesPerHour is the largest count allowed, so the sixth wake in the hour trips.
        for (var i = 0; i < 5; i++)
        {
            _ = guard.Check(new AgentWake("agent-1", WakeKind.Standard));
        }

        var verdict = guard.Check(new AgentWake("agent-1", WakeKind.Standard));

        Assert.Equal(GuardKind.WakesPerHour, verdict.Kind);
    }

    [Fact]
    public void Invalid_configuration_fails_options_validation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{LoopGuardOptions.SectionName}:RepeatToolCall"] = "0",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddBazaarAgentSafety(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<ILoopGuard>());
    }
}
