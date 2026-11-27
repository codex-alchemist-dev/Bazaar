using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.WhatIf;

/// <summary>
/// The What If Engine (docs/design/26-fun-systems.md section 3) as a Bazaar Component. Its SQL is the <c>what_if</c> migration
/// namespace, embedded under <c>Sql/components/what_if</c> and read by the host's component migration source.
/// </summary>
/// <remarks>
/// The sandbox runner (docs 16, 17), the fork source (Time Machine) and the report sink are host-supplied seams and have no default
/// here: a host that has not bound them cannot resolve the engine, which is the safe failure. <see cref="WhatIfRegistration.AddBazaarWhatIfFakes"/>
/// binds the deterministic fakes for tests and development.
/// </remarks>
public sealed class WhatIfComponent : ICdaxModule
{
    /// <summary>Configuration key for <c>what_if.reputation_weight</c>.</summary>
    public const string ReputationWeightConfigKey = "WhatIf:ReputationWeight";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "what-if",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:what-if"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddBazaarWhatIf(configuration);
    }
}

public static class WhatIfRegistration
{
    /// <summary>Registers settings (validated, default 0), the in-memory store unless one is bound, and the engine.</summary>
    public static IServiceCollection AddBazaarWhatIf(this IServiceCollection services, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var weight = 0m;
        if (configuration?[WhatIfComponent.ReputationWeightConfigKey] is { Length: > 0 } text)
        {
            if (!decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out weight))
                throw new FormatException($"{WhatIfSettings.ReputationWeightKey} is not a decimal number.");
        }

        services.TryAddSingleton(new WhatIfSettings(weight));
        services.TryAddSingleton<IWhatIfRunStore, InMemoryWhatIfRunStore>();
        services.TryAddSingleton(sp => new WhatIfEngine(
            sp.GetRequiredService<ISandboxRunner>(),
            sp.GetRequiredService<IWhatIfRunStore>(),
            sp.GetRequiredService<IWhatIfForkSource>(),
            sp.GetRequiredService<IWhatIfReportSink>(),
            sp.GetRequiredService<WhatIfSettings>(),
            sp.GetService<TimeProvider>()));
        return services;
    }

    /// <summary>Binds the Postgres run store. The host must register an <c>NpgsqlDataSource</c> first. Call before <see cref="AddBazaarWhatIf"/>.</summary>
    public static IServiceCollection AddBazaarWhatIfPostgresStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<IWhatIfRunStore, PostgresWhatIfRunStore>());
        return services;
    }

    /// <summary>Binds the deterministic fake runner, fork source and report sink (tests and development only).</summary>
    public static IServiceCollection AddBazaarWhatIfFakes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISandboxRunner, FakeSandboxRunner>();
        services.TryAddSingleton<IWhatIfForkSource, FakeWhatIfForkSource>();
        services.TryAddSingleton<IWhatIfReportSink, InMemoryWhatIfReportSink>();
        return services;
    }
}
