using Bazaar.Secrets;
using Bazaar.Secrets.Keyring;
using Bazaar.Server.Secrets.Postgres;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Bootstrap settings for the secret store (<c>Bazaar:Secrets</c>). The KEK custody choice is the owner's and is parked, so the
/// default is <c>none</c>: no store is registered and anything that needs one fails at start. Grants are deny by default.
/// </summary>
public sealed class SecretStoreOptions
{
    public const string SectionName = "Bazaar:Secrets";

    /// <summary><c>none</c> (default, store off), or <c>secret-service</c> (Linux keyring). <c>dpapi-ng</c> and <c>tpm</c> are reserved and fail the start.</summary>
    public string Kek { get; set; } = "none";

    /// <summary>Item id of the KEK inside the keyring. Stored beside every wrap, so changing it strands existing wraps.</summary>
    public string KekId { get; set; } = "installation-kek-v1";

    public List<SecretGrantOptions> Grants { get; set; } = [];
}

/// <summary>One grant: a component may use a purpose on a scope, optionally for one secret name.</summary>
public sealed class SecretGrantOptions
{
    public string Component { get; set; } = "";

    /// <summary><c>installation</c> or <c>agent:&lt;id&gt;</c>.</summary>
    public string Scope { get; set; } = "installation";

    public string? Name { get; set; }

    public string Purpose { get; set; } = "";
}

public static class SecretStoreCompositionExtensions
{
    /// <summary>
    /// Registers the secret store over PostgreSQL, with the shared audit log and the configured grants. With <c>Kek</c> at <c>none</c>
    /// nothing is registered. Needs <c>AddBazaarGateway</c> (the shared <see cref="NpgsqlDataSource"/>) and the audit component (<see cref="IAuditLog"/>).
    /// </summary>
    public static IServiceCollection AddBazaarSecretStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecretStoreOptions>().Bind(configuration.GetSection(SecretStoreOptions.SectionName));

        var bootstrap = configuration.GetSection(SecretStoreOptions.SectionName).Get<SecretStoreOptions>() ?? new SecretStoreOptions();
        if (bootstrap.Kek == "none")
        {
            return services;
        }

        services.TryAddSingleton<IAgentRegistry>(sp => new PostgresAgentRegistry(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<ITransactionalAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ISecretRecordRepository>(sp => new PostgresSecretRecordRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<ISecretAuditSink>(sp => new AuditLogSecretSink(sp.GetRequiredService<IAuditLog>()));
        services.TryAddSingleton<ISecretPolicy>(sp => BuildPolicy(sp.GetRequiredService<IOptions<SecretStoreOptions>>().Value));
        services.TryAddSingleton<IKekProvider>(sp => OpenKek(sp.GetRequiredService<IOptions<SecretStoreOptions>>().Value));
        services.TryAddSingleton<ISecretStore>(sp => new SecretStore(
            sp.GetRequiredService<IKekProvider>(),
            sp.GetRequiredService<ISecretRecordRepository>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<ISecretPolicy>(),
            sp.GetRequiredService<ISecretAuditSink>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>Builds the grant table. An unparseable grant fails the start rather than being skipped.</summary>
    internal static StaticSecretPolicy BuildPolicy(SecretStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var grants = new List<SecretGrant>(options.Grants.Count);
        foreach (var grant in options.Grants)
        {
            if (string.IsNullOrWhiteSpace(grant.Component) || string.IsNullOrWhiteSpace(grant.Purpose))
            {
                throw new InvalidOperationException("Every Bazaar:Secrets:Grants entry needs a Component and a Purpose.");
            }

            var scopeKey = ParseScopeKey(grant.Scope);
            grants.Add(new SecretGrant(
                grant.Component,
                scopeKey,
                string.IsNullOrEmpty(grant.Name) ? null : new SecretName(grant.Name),
                new SecretPurpose(grant.Purpose)));
        }

        return new StaticSecretPolicy(grants);
    }

    /// <summary>Opens the configured KEK. Explicit choices that cannot run fail the start, with no fallback to another custody.</summary>
    internal static IKekProvider OpenKek(SecretStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Kek switch
        {
            "secret-service" => SecretServiceKekProvider.OpenAsync(new ProcessSecretToolRunner(), options.KekId, CancellationToken.None)
                .GetAwaiter().GetResult(),
            "dpapi-ng" or "tpm" => throw new InvalidOperationException(
                $"Bazaar:Secrets:Kek '{options.Kek}' is reserved and not implemented yet. Use 'secret-service' on Linux, or 'none' to keep the store off."),
            _ => throw new InvalidOperationException($"Unknown Bazaar:Secrets:Kek value '{options.Kek}'. Use 'none' or 'secret-service'."),
        };
    }

    private static string ParseScopeKey(string scope)
    {
        if (scope == "installation")
        {
            return SecretScope.Installation.Key;
        }

        if (scope.StartsWith("agent:", StringComparison.Ordinal))
        {
            return SecretScope.ForAgent(new AgentId(scope["agent:".Length..])).Key;
        }

        throw new InvalidOperationException($"Bazaar:Secrets:Grants scope '{scope}' must be 'installation' or 'agent:<id>'.");
    }
}
