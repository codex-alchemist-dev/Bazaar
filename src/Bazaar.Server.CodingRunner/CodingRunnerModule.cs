using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CodingRunner;

/// <summary>Registration for the coding tool runner.</summary>
public static class CodingRunnerModule
{
    /// <summary>
    /// Registers <see cref="CodingToolRunner"/>. The recorder, the worktree locks and the handlers come from the host; none
    /// is defaulted, so a missing one fails resolution instead of running tools unguarded.
    /// </summary>
    public static IServiceCollection AddBazaarCodingRunner(this IServiceCollection services, CodingToolRunnerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var resolved = options ?? new CodingToolRunnerOptions();
        resolved.Validate();
        services.TryAddSingleton(resolved);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICodingToolRunner>(sp => new CodingToolRunner(
            sp.GetServices<ICodingToolHandler>(),
            sp.GetRequiredService<IToolCallRecorder>(),
            sp.GetRequiredService<IWorktreeLocks>(),
            sp.GetRequiredService<CodingToolRunnerOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
