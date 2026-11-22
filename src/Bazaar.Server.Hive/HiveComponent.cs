using Bazaar.Core;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Memory.Write;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Hive;

/// <summary>
/// Hive Mind (docs/design/25-company-systems-knowledge.md WP7, docs/design/08-memory-system.md section 6) as a Bazaar Component.
/// Host composition is the only caller of <see cref="AddServices"/>. Its SQL is the <c>hive</c> migration namespace, embedded in this
/// assembly under <c>Sql/components/hive</c> and read by the host's component migration source.
/// </summary>
/// <remarks>
/// Memory and Vault are soft dependencies: <see cref="HiveCopyService"/> writes through <see cref="IMemoryBatchSink"/> and reads
/// through <see cref="IHiveNoteReader"/>, which those components provide. Without them the grant store, ledger and policy still
/// resolve, and resolving the copy service fails with a message naming the missing service. The Postgres stores are chosen when
/// the services are resolved: with an <see cref="NpgsqlDataSource"/> registered they are used, otherwise the in-memory ones.
/// </remarks>
public sealed class HiveComponent : IBazaarComponent
{
    /// <summary>Configuration section that binds <see cref="HiveCopyOptions"/>.</summary>
    public const string SectionName = "Bazaar:Hive";

    /// <summary>Migration namespace and history key for this component's SQL. Equals <see cref="ComponentManifest.MigrationNamespace"/>.</summary>
    public const string MigrationProvider = "hive";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "hive",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:hive"],
        SoftDependsOn: ["memory", "vault"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(ReadOptions(configuration.GetSection(SectionName)));

        // In-memory fallbacks are singletons too, so a host without a database keeps its grants between calls.
        services.TryAddSingleton<IHiveGrantStore>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource
                ? new PgHiveGrantStore(dataSource)
                : new InMemoryHiveGrantStore());
        services.TryAddSingleton<IHiveCopyLedger>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource
                ? new PgHiveLedger(dataSource)
                : new InMemoryHiveCopyLedger());

        services.TryAddSingleton<IHiveGrantPolicy>(sp => new HiveGrantPolicy(
            sp.GetRequiredService<IHiveGrantStore>(),
            sp.GetRequiredService<HiveCopyOptions>()));
        services.TryAddSingleton<IHiveGrantApprovals, DenyAllHiveGrantApprovals>();
        services.TryAddSingleton<IHiveGrantWireHandler>(sp => new HiveGrantWireHandler(sp.GetRequiredService<IHiveGrantApprovals>()));
        services.TryAddSingleton(sp => new HiveGrantApplier(
            sp.GetRequiredService<IHiveGrantStore>(),
            sp.GetRequiredService<HiveCopyService>()));
        services.TryAddSingleton(sp => new HiveCopyService(
            sp.GetRequiredService<IHiveGrantPolicy>(),
            sp.GetRequiredService<IHiveGrantStore>(),
            sp.GetRequiredService<IHiveCopyLedger>(),
            sp.GetRequiredService<IHiveNoteReader>(),
            sp.GetRequiredService<IMemoryBatchSink>(),
            sp.GetRequiredService<HiveCopyOptions>(),
            sp.GetService<TimeProvider>()));
    }

    /// <summary>
    /// Reads the operator options. Absent keys keep the defaults of <see cref="HiveCopyOptions"/> (30 review days, procedure and
    /// failure kinds, copies capped at T2). A value that does not parse fails at start, so a typo never loosens a limit.
    /// </summary>
    internal static HiveCopyOptions ReadOptions(IConfigurationSection section)
    {
        var options = new HiveCopyOptions();

        if (section["ReviewDays"] is { Length: > 0 } days)
        {
            if (!int.TryParse(days, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                || parsed is < 0 or > 365)
            {
                throw new InvalidOperationException($"{SectionName}:ReviewDays must be a whole number from 0 to 365.");
            }

            options = options with { ReviewDays = parsed };
        }

        var kindChildren = section.GetSection("AllowedKinds").GetChildren().Select(c => c.Value).ToList();
        if (kindChildren.Count > 0)
        {
            var kinds = new HashSet<HiveKind>();
            foreach (var name in kindChildren)
            {
                if (!HiveKinds.TryParse(name, out var kind))
                {
                    throw new InvalidOperationException(
                        $"{SectionName}:AllowedKinds holds '{name}'. Use procedure, failure or technical-fact.");
                }

                kinds.Add(kind);
            }

            options = options with { AllowedKinds = kinds };
        }

        if (section["WriterAgentId"] is { Length: > 0 } writer)
        {
            options = options with { WriterAgentId = writer };
        }

        if (section["MaxCopyTier"] is { Length: > 0 } tier)
        {
            if (!Enum.TryParse<MemoryTier>(tier, ignoreCase: true, out var parsedTier) || !Enum.IsDefined(parsedTier))
            {
                throw new InvalidOperationException($"{SectionName}:MaxCopyTier '{tier}' is not a memory tier name.");
            }

            // Copies never promote themselves (doc 25), so T2 is a floor of protection: the setting may only lower the cap.
            if (parsedTier > MemoryTier.AgentVerified)
            {
                throw new InvalidOperationException($"{SectionName}:MaxCopyTier may not be above {MemoryTier.AgentVerified}.");
            }

            options = options with { MaxCopyTier = parsedTier };
        }

        return options;
    }
}
