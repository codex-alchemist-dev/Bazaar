using System.Globalization;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Identity;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Coding;

/// <summary>Registers the host sources for <see cref="IAgentGitIdentity"/>, <see cref="IAgentStateReader"/> and <see cref="IPushSessionRecords"/>.</summary>
public static class CodingHarnessAgentPortsExtensions
{
    /// <summary>Configuration key of the store-read timeout in seconds.</summary>
    public const string ReadTimeoutKey = "Coding:Harness:StateReadTimeoutSeconds";

    /// <summary>
    /// Registers the three sources when <c>Coding:Harness:Enabled</c> is true; registers nothing otherwise. Throws at registration,
    /// naming the key, when <c>Coding:Harness:Git:AppNoreplyEmail</c> or the timeout is bad. A registration the host made first wins
    /// (TryAdd). Call it with the same configuration as <c>AddBazaarCodingHarness</c>; the startup check then finds the ports bound.
    /// </summary>
    public static IServiceCollection AddBazaarCodingHarnessAgentPorts(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!(bool.TryParse(configuration["Coding:Harness:Enabled"], out var enabled) && enabled))
        {
            return services;
        }

        var options = new AgentGitIdentityOptions { AppNoreplyEmail = configuration[AgentGitIdentityOptions.AppNoreplyEmailKey] };
        options.Validate();

        var names = configuration.GetSection(AgentGitIdentityOptions.SectionName + ":AgentNames").GetChildren()
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);
        var timeout = ReadTimeout(configuration);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IAgentDisplayNames>(_ => new ConfiguredAgentDisplayNames(names));
        services.TryAddSingleton<IAgentGitIdentity>(sp => new ConfiguredAgentGitIdentity(
            sp.GetRequiredService<IAgentDisplayNames>(),
            sp.GetRequiredService<AgentGitIdentityOptions>()));
        services.TryAddSingleton<IAgentStateReader>(sp => new AgentStateReader(
            sp.GetRequiredService<IQuarantineStore>(),
            sp.GetRequiredService<IAgentFreeze>(),
            sp.GetRequiredService<IRunningTurnIndex>(),
            sp.GetRequiredService<ISessionTaintStore>(),
            timeout));
        services.TryAddSingleton<IPushSessionRecords>(sp => new RunningTurnPushSessionRecords(
            sp.GetRequiredService<IRunningTurnIndex>(),
            sp.GetRequiredService<ISessionTaintStore>(),
            timeout));
        return services;
    }

    private static TimeSpan ReadTimeout(IConfiguration configuration)
    {
        var raw = configuration[ReadTimeoutKey];
        if (raw is null)
        {
            return TimeSpan.FromSeconds(2);
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds is <= 0 or > 60)
        {
            throw new InvalidOperationException($"{ReadTimeoutKey} must be a number of seconds above 0 and at most 60.");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
