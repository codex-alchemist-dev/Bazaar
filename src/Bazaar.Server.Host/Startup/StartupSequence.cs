using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Providers;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// Doc 03 startup steps 3 to 5, run before the other hosted services: take the instance lock (a second instance exits with one log line),
/// check the schema, check that the host KEK opens the keys already stored, probe providers. The schema gate is a seam; M1's runner plugs in through <see cref="ISchemaGate"/>.
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
    IKekCustodyCheck? kekCustody = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await instanceLock.TryAcquireAsync(cancellationToken))
            {
                LogSecondInstance(logger);
                exitCode.Value = 1;
                lifetime.StopApplication();
                throw new StartupRefusedException("another Bazaar.Server instance holds the instance lock for this database");
            }

            await schemaGate.EnsureCurrentAsync(cancellationToken);

            // The KEK must be the one this database's keys were wrapped under. A wrong or unmounted data root gives a different
            // (or freshly made) key file: the host would run, fail every encrypted read and write, and still report ready.
            if (kekCustody is not null && await kekCustody.CheckAsync(cancellationToken) is { Matches: false } custody)
            {
                var reason = $"the host key file does not open the keys stored in this database (tried {custody.Stored}, opened none). "
                    + "The data root is probably not the one this database was set up with, or it is not mounted. "
                    + "Point Bazaar:Host:DataRoot at the right folder or restore its keys folder; a key file made by this start is not the key for this database. "
                    + "Nothing in the database was changed";
                LogStartRefused(logger, reason);
                exitCode.Value = 1;
                lifetime.StopApplication();
                throw new StartupRefusedException(reason);
            }
        }
        catch (Exception ex) when (ex is not StartupRefusedException && (ex is MigrationException || MigrateCommand.TryDescribeDatabaseFailure(ex, out _)))
        {
            // A refusal is an expected outcome: one log line and exit code 1, not an unhandled-exception crash. Throwing (not just stopping the
            // application) keeps the later hosted services, such as the recovery sweep, from running against a database this build must not touch.
            var reason = ex is MigrationException ? ex.Message : Describe(ex);
            LogStartRefused(logger, reason);
            exitCode.Value = 1;
            lifetime.StopApplication();
            throw new StartupRefusedException(reason);
        }

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

    private static string Describe(Exception ex) => MigrateCommand.TryDescribeDatabaseFailure(ex, out var line) ? line : ex.Message;

    [LoggerMessage(Level = LogLevel.Critical, Message = "Startup refused: {Reason}")]
    private static partial void LogStartRefused(ILogger logger, string reason);

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

/// <summary>Startup stopped on purpose (second instance, schema mismatch, database unreachable). The reason is already logged; the process exits with code 1.</summary>
public sealed class StartupRefusedException(string message) : Exception(message);

/// <summary>
/// Runs the host to its end and returns the process exit code. A stop that was asked for is never a crash: a stop requested while
/// the host is still starting (the second instance, or SIGTERM during start) cancels the start token, so the next hosted service
/// throws <see cref="OperationCanceledException"/> out of <c>RunAsync</c>; and a stop that overruns the host's shutdown timeout
/// cancels the stop token, so any <c>StopAsync</c> that honours it throws the same way. Unhandled, either ended the process with
/// SIGABRT (exit 134) instead of the designed exit code (chaos finding, crash-only campaign).
/// </summary>
public static partial class HostRun
{
    public static async Task<int> RunAsync(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        // Resolve before RunAsync: the container is disposed when the host stops, so resolving afterwards throws.
        var exitCode = host.Services.GetRequiredService<HostExitCode>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HostRun).FullName!);
        try
        {
            await host.RunAsync().ConfigureAwait(false);
        }
        catch (StartupRefusedException)
        {
            // Logged by StartupSequence; a deliberate refusal exits with code 1 and no crash dump.
            return 1;
        }
        catch (Exception ex) when (lifetime.ApplicationStopping.IsCancellationRequested && OnlyCancellations(ex))
        {
            LogStopCancelled(logger);
        }

        return exitCode.Value;
    }

    // Several hosted services that each honour the cancelled stop token arrive as one AggregateException.
    private static bool OnlyCancellations(Exception ex) => ex switch
    {
        OperationCanceledException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(OnlyCancellations),
        _ => false,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host stopped before a hosted service finished starting or stopping; work it did not finish is recovered on the next start.")]
    private static partial void LogStopCancelled(ILogger logger);
}
