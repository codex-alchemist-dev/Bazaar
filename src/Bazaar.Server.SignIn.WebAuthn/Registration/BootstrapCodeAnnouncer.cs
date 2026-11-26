using Bazaar.Server.Security.Audit;
using Bazaar.Server.SignIn.WebAuthn.Credentials;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.SignIn.WebAuthn.Registration;

/// <summary>
/// At start, when the operator named a bootstrap principal and that principal has no passkey, issues the one-time bootstrap code and
/// hands it to <see cref="IBootstrapCodeSink"/>. The issue is audited (the code itself never is). If the credential store cannot be
/// read, no code is issued: the sign-in page stays closed to registration rather than guessing.
/// </summary>
public sealed class BootstrapCodeAnnouncer(
    WebAuthnOptions options,
    IPasskeyCredentialStore credentials,
    BootstrapCodeService bootstrap,
    IBootstrapCodeSink sink,
    TimeProvider time,
    ILogger<BootstrapCodeAnnouncer> logger,
    IAuditLog? audit = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.IsConfigured || !options.BootstrapEnabled || options.BootstrapPrincipalId is not { } principal || audit is null)
        {
            return;
        }

        try
        {
            if (await credentials.CountAsync(principal, cancellationToken).ConfigureAwait(false) > 0)
            {
                return;
            }

            var code = bootstrap.Issue(principal);
            await audit.AppendAsync(
                new AuditEntry(time.GetUtcNow(), "system", "signin.bootstrap.issued", $"{{\"principal\":\"{principal:D}\"}}"),
                cancellationToken).ConfigureAwait(false);
            sink.Announce(code, principal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without an audit row or a readable store the code is not shown (it was never revealed, so drop it).
            bootstrap.TryConsume(principal);
            WebAuthnLog.BootstrapNotIssued(logger, ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
