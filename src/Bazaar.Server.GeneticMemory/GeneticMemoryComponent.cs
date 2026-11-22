using Bazaar.Core;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.GeneticMemory.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.GeneticMemory;

/// <summary>
/// Genetic Memory Transfer (docs/design/25-company-systems-knowledge.md work package 9) as a Bazaar Component. Memory is a soft
/// dependency: the ledger and options always resolve, and the service resolves once <see cref="IGmtLessonWriter"/> exists (the memory component provides it).
/// </summary>
public sealed class GeneticMemoryComponent : IBazaarComponent
{
    /// <summary>Configuration section that binds <see cref="GmtOptions"/>.</summary>
    public const string SectionName = "Bazaar:GeneticMemory";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "genetic-memory",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:genetic-memory"],
        SoftDependsOn: ["memory", "vault"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(ReadOptions(configuration.GetSection(SectionName)));
        // With an NpgsqlDataSource the queue survives restarts; without one the in-memory ledger keeps a dev host working.
        services.TryAddSingleton<IGmtLedger>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource ? new PgGmtLedger(dataSource) : new InMemoryGmtLedger());
        services.TryAddSingleton(sp => new GeneticMemoryService(
            sp.GetRequiredService<IGmtLedger>(),
            sp.GetRequiredService<IGmtLessonWriter>(),
            sp.GetRequiredService<IGmtSecretScanner>(),
            sp.GetRequiredService<GmtOptions>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IGeneticMemoryTransfer>(sp => sp.GetRequiredService<GeneticMemoryService>());

        // The child-end hook (doc 06): added only when the host provides a proposal source, so an install without one stages nothing.
        services.AddSingleton<ISubagentEndHook>(sp => sp.GetService<IGmtProposalSource>() is { } source
            ? new GmtSubagentEndHook(source, sp.GetRequiredService<IGeneticMemoryTransfer>())
            : new NoGmtHook());
    }

    /// <summary>Reads <c>Reviewer</c> (architect or owner) and <c>WriterAgentId</c>. A value that does not parse fails at start.</summary>
    internal static GmtOptions ReadOptions(IConfigurationSection section)
    {
        var options = new GmtOptions();
        if (section["Reviewer"] is { Length: > 0 } reviewer)
        {
            if (!Enum.TryParse<GmtReviewer>(reviewer, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                throw new InvalidOperationException($"{SectionName}:Reviewer '{reviewer}' must be architect or owner.");
            }

            options = options with { LessonReviewer = parsed };
        }

        if (section["WriterAgentId"] is { Length: > 0 } writer)
        {
            options = options with { WriterAgentId = writer };
        }

        return options;
    }

    private sealed class NoGmtHook : ISubagentEndHook
    {
        public Task OnSubagentEndedAsync(SubagentEnded ended, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
