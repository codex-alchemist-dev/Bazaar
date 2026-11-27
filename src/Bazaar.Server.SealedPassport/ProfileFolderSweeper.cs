using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.SealedPassport;

/// <summary>Runs the stale plaintext profile folder sweep once when the host starts (doc 61 Passport adoption).</summary>
internal sealed class ProfileFolderSweeper(ISealedProfileDirectories folders) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (folders is SealedProfileDirectories directories)
        {
            await directories.SweepStaleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
