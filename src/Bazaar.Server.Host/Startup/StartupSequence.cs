using Bazaar.Server.Providers;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// Doc 03 startup steps 3 to 5, run before the other hosted services: take the instance lock (a second instance exits with one log line),
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
    ILogger<StartupSequence> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
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
