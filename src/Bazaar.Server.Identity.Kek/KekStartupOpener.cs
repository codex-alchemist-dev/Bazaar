using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Identity.Kek;

/// <summary>
/// Opens the KEK provider at host start (doc 14). Resolving <see cref="IKekProvider"/> opens it, so a fresh install creates its key
/// file (owner-only on POSIX) before anything needs it, and a bad provider setting fails the start instead of the first wrap.
/// It lives in the KEK module, so Bazaar.Server takes no feature reference.
/// </summary>
public sealed class KekStartupOpener(IKekProvider kek) : IHostedService
{
    /// <summary>The id of the opened KEK, for tests and diagnostics. Never key material.</summary>
    public string KekId => kek.KekId;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
