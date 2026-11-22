using Bazaar.Server.CodingGit;
using Bazaar.Server.Identity;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// The ports the harness needs from the host when <c>Coding:Harness:Enabled</c> is true, and the check that they are bound.
/// These are the ports whose real source does not exist in the repo yet, so the harness cannot run without a host binding.
/// The check looks for a registration of each type; it does not resolve them.
/// </summary>
public static class CodingHarnessPorts
{
    /// <summary>The required ports, in check order.</summary>
    public static IReadOnlyList<Type> Required { get; } =
    [
        typeof(ICodingRepoDirectory),
        typeof(IWorkShareLayout),
        typeof(ISubagentWorktreeAllocator),
        typeof(IAgentGitIdentity),
        typeof(IRuntimeSubagentParentDirectory),
        typeof(IAgentStateReader),
        typeof(IPushSessionRecords),
        typeof(ICodingAgentFacts),
        typeof(ICodingSessionParents),
    ];

    /// <summary>Throws, naming the first port with no registration. Fails closed: the harness never starts half-bound.</summary>
    /// <exception cref="InvalidOperationException">A required port is not bound.</exception>
    public static void VerifyBound(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var port in Required)
        {
            if (!services.Any(descriptor => descriptor.ServiceType == port))
            {
                throw new InvalidOperationException(
                    $"Coding:Harness:Enabled is true, but the port {port.Name} is not bound. Bind it in the host, or set Coding:Harness:Enabled to false.");
            }
        }
    }
}

/// <summary>Runs <see cref="CodingHarnessPorts.VerifyBound"/> at host start, so a half-bound harness stops the host with a named port.</summary>
internal sealed class CodingHarnessStartupCheck(IServiceCollection services) : IHostedService
{
    private readonly IServiceCollection _services = services;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        CodingHarnessPorts.VerifyBound(_services);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
