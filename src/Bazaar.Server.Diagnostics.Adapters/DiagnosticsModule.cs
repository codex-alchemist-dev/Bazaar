using Bazaar.Diagnostics.Ingestion;
using Bazaar.Diagnostics.Redaction;
using Bazaar.Erasure.Abstractions;
using Bazaar.Server.Diagnostics.Persistence;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Diagnostics.Adapters;

/// <summary>Builds the intake service for one authenticated connection, which is what decides install ownership.</summary>
public sealed class CrashIntakeFactory(ICrashIntakeStore store, ICrashPayloadSealer sealer, IAuditLog audit, ISymbolicator symbolicator, TimeProvider clock, CrashFilingOptions options)
{
    /// <summary>Creates the service for a connection whose devices own the installs <paramref name="ownership"/> accepts.</summary>
    public CrashIntakeService Create(IInstallOwnershipResolver ownership) =>
        new(new CrashIngestionPipeline(ownership, new Redactor(), symbolicator), store, sealer, audit, clock, options);
}

/// <summary>The filer used until the owner registers the system GitHub App: every call fails, so the outbox keeps its rows and backs off.</summary>
public sealed class UnavailableCrashIssueFiler : ICrashIssueFiler
{
    /// <inheritdoc />
    public Task<int> CreateIssueAsync(Bazaar.Diagnostics.Grouping.IssueText issue, CancellationToken ct = default) => throw Unavailable();

    /// <inheritdoc />
    public Task CommentAsync(int issueNumber, string text, CancellationToken ct = default) => throw Unavailable();

    /// <inheritdoc />
    public Task ReopenAsync(int issueNumber, string text, CancellationToken ct = default) => throw Unavailable();

    private static InvalidOperationException Unavailable() => new("The system GitHub App is not configured.");
}

/// <summary>The budget used until the quota service (doc 05, doc 15) is bound: nothing is reserved, so diagnosis stays paused.</summary>
public sealed class UnavailableDiagnosisBudget : ICrashDiagnosisBudget
{
    /// <inheritdoc />
    public Task<bool> TryReserveAsync(long estimatedTokens, CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc />
    public Task SettleAsync(long reservedTokens, long usedTokens, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>The symbol lookup used until the release symbol bundles of doc 37 exist: every report is kept with symbols marked missing.</summary>
public sealed class NoSymbolsSymbolicator : ISymbolicator
{
    /// <inheritdoc />
    public SymbolsState Resolve(Bazaar.Contracts.Telemetry.CrashReport report) => SymbolsState.Missing;
}

/// <summary>Composition of the crash diagnostics module. The host calls it once.</summary>
public static class DiagnosticsModule
{
    /// <summary>The configuration section the crash options are read from, until doc 03's settings registry hook exists.</summary>
    public const string ConfigurationSection = "Diagnostics:Crash";

    /// <summary>Registry key <c>telemetry.crash.diagnosis</c> (doc 43 section 7) and the config key that stands in for it.</summary>
    public const string DiagnosisSettingKey = "telemetry.crash.diagnosis";

    /// <summary>
    /// Registers the store, sealer, intake factory, dispatcher, erasure participant and maintenance service. Expects the shared
    /// <c>NpgsqlDataSource</c>, <see cref="ICrashKeyStore"/> and <see cref="IAuditLog"/> to be registered. The GitHub filer and the
    /// install directory are seams: until the host registers them, filing stays off and erasure fails closed.
    /// </summary>
    public static IServiceCollection AddBazaarDiagnosticsModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(ConfigurationSection);
        var options = new CrashFilingOptions
        {
            IssueRepo = section["IssueRepo"] ?? string.Empty,
            IssuesPerDay = section.GetValue("IssuesPerDay", 20),
        };
        services.TryAddSingleton(options);
        services.TryAddSingleton(CrashRetentionPolicy.Default);
        services.TryAddSingleton(CrashSealingOptions.Default);
        services.TryAddSingleton<ISymbolicator, NoSymbolsSymbolicator>();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<PostgresCrashInstallDirectory>();
        services.TryAddSingleton<ICrashInstallDirectory>(sp => sp.GetRequiredService<PostgresCrashInstallDirectory>());
        services.TryAddSingleton<ICrashInstallRegistry>(sp => sp.GetRequiredService<PostgresCrashInstallDirectory>());
        services.TryAddSingleton<PostgresCrashStore>();
        services.TryAddSingleton<ICrashIntakeStore>(sp => sp.GetRequiredService<PostgresCrashStore>());
        services.TryAddSingleton<ICrashActionOutbox>(sp => sp.GetRequiredService<PostgresCrashStore>());
        services.TryAddSingleton<ICrashRetention>(sp => sp.GetRequiredService<PostgresCrashStore>());
        services.TryAddSingleton<ICrashErasure>(sp => sp.GetRequiredService<PostgresCrashStore>());
        services.TryAddSingleton<ICrashPayloadSealer, PersonKeyCrashSealer>();
        services.TryAddSingleton<CrashIntakeFactory>();
        services.TryAddSingleton<ICrashIssueFiler>(sp =>
            sp.GetService<ICrashGitHubTokenSource>() is { } tokens
                ? new GitHubCrashIssueFiler(
                    new HttpClient { BaseAddress = new Uri("https://api.github.com/") }, tokens, sp.GetRequiredService<CrashFilingOptions>())
                : new UnavailableCrashIssueFiler());
        services.TryAddSingleton<CrashActionDispatcher>();
        services.TryAddSingleton(new CrashDiagnosisOptions(
            Enum.TryParse<CrashDiagnosisMode>(section["Diagnosis"], ignoreCase: true, out var mode) ? mode : CrashDiagnosisMode.Ask,
            section.GetValue("PostDiagnosisToIssue", false)));
        services.TryAddSingleton<ICrashDiagnosisBudget, UnavailableDiagnosisBudget>();
        services.TryAddSingleton<ICrashDiagnosisStore>(sp => sp.GetRequiredService<PostgresCrashStore>());
        services.TryAddSingleton<ICrashReportReader>(sp => sp.GetRequiredService<PostgresCrashStore>());
        services.TryAddSingleton(sp => new CrashDiagnosisService(
            sp.GetRequiredService<ICrashIntakeStore>(),
            sp.GetRequiredService<ICrashReportReader>(),
            sp.GetRequiredService<ICrashPayloadSealer>(),
            sp.GetService<ICrashDiagnosisModel>(),
            sp.GetRequiredService<ICrashDiagnosisBudget>(),
            sp.GetRequiredService<ICrashDiagnosisStore>(),
            sp.GetRequiredService<ICrashIssueFiler>(),
            sp.GetRequiredService<IAuditLog>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<CrashDiagnosisOptions>()));
        services.AddSingleton<IErasureParticipant, CrashErasureParticipant>();
        services.AddSingleton<Bazaar.Server.Wire.Connection.IWireRequestHandler, CrashSubmitHandler>();
        services.AddHostedService<CrashMaintenanceService>();
        return services;
    }
}
