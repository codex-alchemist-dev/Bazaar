using Bazaar.Server.Setup.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Setup.Host;

/// <summary>
/// Shows a new bootstrap code to the operator. The code is shown once and never logged (doc 45 section 1.9). The host supplies the
/// implementation (setup modules use no console, doc 45 section 1.1); a host that ships <see cref="SetupComponent"/> must register one.
/// </summary>
public interface IBootstrapCodeAnnouncer
{
    /// <summary>Shows <paramref name="code"/> to whoever started the server.</summary>
    void Announce(string code);
}

/// <summary>
/// At start, when no owner exists, creates a fresh one-time bootstrap code (replacing any earlier one) and announces it. When an
/// owner exists, retires any code left over. A store failure is logged by exception type only and does not stop the server: the
/// provisioning routes then refuse every call, because no code exists.
/// </summary>
public sealed partial class SetupBootstrapCodeIssuer(
    IBootstrapCodeStore codes,
    ISetupOwnerDirectory owners,
    IBootstrapCodeAnnouncer announcer,
    ILogger<SetupBootstrapCodeIssuer> logger) : IHostedService
    ILogger<SetupBootstrapCodeIssuer> logger,
    SetupPlanStartOutcome? planStart = null) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // A --setup-only run stops after the plan apply, so it announces no code.
        if (planStart is { StopRequested: true })
        {
            return;
        }

        try
        {
            if (await owners.OwnerExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                await codes.RevokeAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            announcer.Announce(await codes.CreateAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogIssueFailed(logger, exception.GetType().Name);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup bootstrap code was not issued ({ExceptionType}); the provisioning API stays closed until it is.")]
    private static partial void LogIssueFailed(ILogger logger, string exceptionType);
}
