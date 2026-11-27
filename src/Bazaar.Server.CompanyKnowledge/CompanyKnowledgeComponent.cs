using Cdax.Core;
using Bazaar.Server.CompanyKnowledge.Abstractions;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.CompanyKnowledge;

/// <summary>
/// The company knowledge tier (docs/design/08-memory-system.md, section 6, PROPOSED) as a Bazaar Component: the deterministic half only
/// (note shape, staging, admission, access sources). The distillation job that calls a model is <see cref="ICompanyDistiller"/>, which this
/// component does not implement. Memory is a soft dependency: <see cref="CompanyKnowledgeService"/> resolves once the memory component
/// provides <see cref="IMemoryVault"/>.
/// </summary>
public sealed class CompanyKnowledgeComponent : ICdaxModule
{
    /// <summary>Configuration section that binds <see cref="CompanyKnowledgeOptions"/>.</summary>
    public const string SectionName = "Bazaar:CompanyKnowledge";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "company-knowledge",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:company-knowledge"],
        SoftDependsOn: ["memory", "vault"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(ReadOptions(configuration.GetSection(SectionName)));
        services.TryAddSingleton<CompanyCandidateRegistry>();

        // The same registry answers the access rule, so VaultPathAccess (built by the host) can resolve it.
        services.TryAddSingleton<ICompanyCandidateProposers>(sp => sp.GetRequiredService<CompanyCandidateRegistry>());
        services.TryAddSingleton<ICompanySanitizerTerms, EmptyCompanySanitizerTerms>();

        // Quarantine state comes from the runtime when it registers an IAgentStateReader. With none, agent proposals are refused (fail closed).
        services.TryAddSingleton<ICompanyAgentQuarantine>(sp =>
            sp.GetService<Bazaar.Server.Identity.IAgentStateReader>() is { } state
                ? new AgentStateCompanyQuarantine(state)
                : new AllQuarantinedCompanyQuarantine());
        services.TryAddSingleton<ICompanyKnowledgeAudit, NullCompanyKnowledgeAudit>();
        services.TryAddSingleton(sp => new CompanyKnowledgeService(
            sp.GetService<IMemoryVault>() ?? throw new InvalidOperationException(
                "CompanyKnowledgeService needs IMemoryVault, which the memory component registers when Memory:Vault:RepoPath is set."),
            sp.GetService<IVaultAccess>() ?? new VaultPathAccess(
                new StaticProjectMembership(new Dictionary<string, ProjectMembers>())),
            sp.GetRequiredService<CompanyCandidateRegistry>(),
            sp.GetRequiredService<CompanyKnowledgeOptions>(),
            sp.GetRequiredService<ICompanySanitizerTerms>(),
            sp.GetService<ICompanyAgentQuarantine>(),
            sp.GetRequiredService<ICompanyKnowledgeAudit>(),
            sp.GetService<TimeProvider>()));
    }

    /// <summary>
    /// Reads the operator options. Absent keys keep the defaults. A value that does not parse, or that would loosen what doc 08 fixes
    /// (a negative window, an <c>Admission</c> below the review window, fewer than one confirming project), fails at start.
    /// </summary>
    internal static CompanyKnowledgeOptions ReadOptions(IConfigurationSection section)
    {
        var options = new CompanyKnowledgeOptions();
        options = options with
        {
            ReviewDays = Int(section, "ReviewDays", options.ReviewDays, 0, 365),
            ConfirmedMinProjects = Int(section, "ConfirmedMinProjects", options.ConfirmedMinProjects, 1, 100),
            ReviewIntervalDaysT2 = Int(section, "ReviewIntervalDaysT2", options.ReviewIntervalDaysT2, 1, 3650),
            ReviewIntervalDaysT3 = Int(section, "ReviewIntervalDaysT3", options.ReviewIntervalDaysT3, 1, 3650),
            MaxProposalsPerAgentWeek = Int(section, "MaxProposalsPerAgentWeek", options.MaxProposalsPerAgentWeek, 0, 1000),
        };

        if (section["Admission"] is { Length: > 0 } admission)
        {
            options = options with
            {
                MinAuthority = admission switch
                {
                    "review-window" => CompanyAdmissionAuthority.ReviewWindow,
                    "council" => CompanyAdmissionAuthority.Council,
                    "owner" => CompanyAdmissionAuthority.Owner,
                    _ => throw new InvalidOperationException($"{SectionName}:Admission '{admission}' is not review-window, council or owner."),
                },
            };
        }

        return section["WriterAgentId"] is { Length: > 0 } writer ? options with { WriterAgentId = writer } : options;
    }

    private static int Int(IConfigurationSection section, string key, int fallback, int min, int max)
    {
        if (section[key] is not { Length: > 0 } text)
        {
            return fallback;
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new InvalidOperationException($"{SectionName}:{key} must be a whole number from {min} to {max}.");
        }

        return value;
    }
}

/// <summary><see cref="ICompanyAgentQuarantine"/> over the runtime's <see cref="Bazaar.Server.Identity.IAgentStateReader"/>.</summary>
public sealed class AgentStateCompanyQuarantine(Bazaar.Server.Identity.IAgentStateReader state) : ICompanyAgentQuarantine
{
    /// <inheritdoc />
    public bool IsQuarantined(string agentId) => state.IsQuarantined(agentId);
}

/// <summary>The fail-closed default when the host has no agent state: every agent counts as quarantined, so no agent proposes and none reads <c>company/</c> through this source.</summary>
public sealed class AllQuarantinedCompanyQuarantine : ICompanyAgentQuarantine
{
    /// <inheritdoc />
    public bool IsQuarantined(string agentId) => true;
}
