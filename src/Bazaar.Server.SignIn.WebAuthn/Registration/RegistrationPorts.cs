using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.SignIn.WebAuthn.Registration;

/// <summary>
/// Names the human who is already signed in on a request, so they can enrol another passkey. The host supplies the adapter
/// (for example over the admin panel session). The default resolves nobody, so only the bootstrap code can register until one is set.
/// </summary>
public interface IRegistrationPrincipalResolver
{
    /// <summary>The signed-in human's principal id, or null when the request carries no valid session.</summary>
    Task<Guid?> ResolveAsync(HttpContext context, CancellationToken ct = default);
}

public sealed class NoSignedInPrincipalResolver : IRegistrationPrincipalResolver
{
    public Task<Guid?> ResolveAsync(HttpContext context, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
}

/// <summary>Where the one-time bootstrap code is shown to the operator. The default writes to the server's standard error, never to a log.</summary>
public interface IBootstrapCodeSink
{
    void Announce(string code, Guid principalId);
}

public sealed class ConsoleBootstrapCodeSink : IBootstrapCodeSink
{
    public void Announce(string code, Guid principalId)
    {
        // Written straight to the console rather than through ILogger, so the code is not kept by a log sink.
        Console.Error.WriteLine("Bazaar sign-in bootstrap: one-time code to enrol the first passkey for principal " + principalId.ToString("D") + " (loopback only):");
        Console.Error.WriteLine("  " + code);
    }
}
