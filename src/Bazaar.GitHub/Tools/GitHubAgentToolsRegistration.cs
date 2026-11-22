using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.GitHub.Tools;

/// <summary>Registers the agent GitHub tools with the turn path (doc 21, Agent tools [PROPOSED]).</summary>
public static class GitHubAgentToolsRegistration
{
    /// <summary>Policy entry for <c>github.pr_read</c>: a read, autonomous, from any session role.</summary>
    public static ToolCapability ReadCapability { get; } = new(
        GitHubPrReadTool.ToolName,
        ToolTier.Autonomous,
        CapabilityClass.Read,
        SessionRoles.Reader | SessionRoles.Actor,
        new RateRule(120, TimeSpan.FromHours(1)),
        ToolResultTaint.Hard);

    /// <summary>Policy entry for <c>github.pr_review</c>: a post under the agent's own App, autonomous with a notice, actor sessions only.</summary>
    public static ToolCapability ReviewCapability { get; } = new(
        GitHubPrReviewTool.ToolName,
        ToolTier.AutonomousWithNotice,
        CapabilityClass.Post,
        SessionRoles.Actor,
        new RateRule(30, TimeSpan.FromHours(1)));

    /// <summary>
    /// Adds both tools, their policy entries and model definitions. Both need <c>IGitHubAdapter</c> and <c>IGitHubTokenProvider</c>
    /// (from <c>AddBazaarGitHub</c>). With no token source configured, a call returns a refusal and sends nothing.
    /// </summary>
    public static IServiceCollection AddBazaarGitHubAgentTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IGitHubAgentInstallations>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgGitHubAgentInstallations(dataSource) : new NoGitHubAgentInstallations());
        services.TryAddSingleton<GitHubPrReadTool>();
        services.TryAddSingleton<GitHubPrReviewTool>();
        services.AddSingleton(sp => new ToolRegistration(GitHubPrReadTool.ToolName, sp.GetRequiredService<GitHubPrReadTool>()));
        services.AddSingleton(sp => new ToolRegistration(GitHubPrReviewTool.ToolName, sp.GetRequiredService<GitHubPrReviewTool>()));
        services.AddSingleton(ReadCapability);
        services.AddSingleton(ReviewCapability);
        services.AddSingleton(GitHubPrReadTool.Definition);
        services.AddSingleton(GitHubPrReviewTool.Definition);
        return services;
    }
}
