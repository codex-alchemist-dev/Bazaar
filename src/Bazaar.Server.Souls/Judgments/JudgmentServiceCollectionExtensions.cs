using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Souls.Judgments;

/// <summary>Registration for the judgment writer. The host must also register <see cref="IDecisionFileWriter"/>.</summary>
public static class JudgmentServiceCollectionExtensions
{
    // TODO(component): move to AddBazaarSouls() when the component model (PR #125) merges.
    /// <summary>
    /// Registers <see cref="JudgmentWriter"/> over the GC log writer (<c>AddGcLog</c>) and the body codec. There is no default
    /// <see cref="IDecisionFileWriter"/>; it comes from the Vault component (work package 18 WP4).
    /// </summary>
    public static IServiceCollection AddJudgmentWriter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IGcJudgmentPoster, GcLogJudgmentPoster>();
        services.AddSingleton<JudgmentWriter>();
        return services;
    }
}
