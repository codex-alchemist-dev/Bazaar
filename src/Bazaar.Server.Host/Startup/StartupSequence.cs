using Bazaar.Server.DataVolume;
using Bazaar.Server.Providers;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// Doc 03 startup steps, run before the other hosted services: verify the data volume when a guard is registered (doc 63
/// section 5; a refusal stops the host with exit code 78 before the lock is taken), then take the instance lock (a second instance exits with one log line),
/// check the schema, probe providers. The schema gate is a seam; M1's runner plugs in through <see cref="ISchemaGate"/>.
/// </summary>
public sealed partial class StartupSequence(
    IInstanceLock instanceLock,
    ISchemaGate schemaGate,
    ProviderCapabilityReport providers,
    IOptions<BazaarReadinessOptions> readiness,
    HostState state,
    HostExitCode exitCode,
    IHostApplicationLifetime lifetime,
    ILogger<StartupSequence> logger,
    IEnumerable<IDataVolumeGuard>? dataVolumeGuards = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var guard in dataVolumeGuards ?? [])
        {
            try
            {
                await guard.VerifyAsync(cancellationToken);
            }
            catch (DataVolumeRefusedException refusal)
            {
                // Fail closed: nothing after this step runs, so no lock is taken and no schema is touched.
                LogDataVolumeRefused(logger, refusal.Code, refusal.Message);
                exitCode.Value = DataVolumeRefusedException.ExitCode;
                lifetime.StopApplication();
                return;
            }
        }

        if (!await instanceLock.TryAcquireAsync(cancellationToken))
        {
            LogSecondInstance(logger);
            exitCode.Value = 1;
            lifetime.StopApplication();
            return;
        }

        await schemaGate.EnsureCurrentAsync(cancellationToken);

        foreach (var line in providers.Lines)
        {
            LogProvider(logger, line);
        }

        var missing = providers.MissingRequiredRoles(readiness.Value.RequiredRoles);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException("Required provider roles are not usable: " + string.Join(", ", missing.Select(r => r.SettingName())));
        }

        state.MarkStarted();
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await instanceLock.DisposeAsync();

    [LoggerMessage(Level = LogLevel.Critical, Message = "Another Bazaar.Server instance holds the instance lock for this database; exiting.")]
    private static partial void LogSecondInstance(ILogger logger);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Data volume check refused the start ({Code}): {Reason}")]
    private static partial void LogDataVolumeRefused(ILogger logger, string code, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Line}")]
    private static partial void LogProvider(ILogger logger, string line);
}

/// <summary>Startup step 4: refuse to start unless the database schema matches the build. Throws on mismatch in either direction.</summary>
public interface ISchemaGate
{
    Task EnsureCurrentAsync(CancellationToken ct);
}

/// <summary>Exit code the process returns; set to 1 when startup stops the host on purpose.</summary>
public sealed class HostExitCode
{
    public int Value { get; set; }
}
