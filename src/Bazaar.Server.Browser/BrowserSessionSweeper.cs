using Bazaar.Server.Browser.Abstractions;

namespace Bazaar.Server.Browser;

/// <summary>
/// Ends browser sessions older than <see cref="BrowserOptions.EffectiveMaxSessionAge"/> so a forgotten session cannot
/// hold an account cookie jar forever. The host calls <see cref="SweepAsync"/> on a timer.
/// </summary>
public sealed class BrowserSessionSweeper(IBrowserLauncher launcher, BrowserOptions options, TimeProvider time)
{
    /// <summary>Ends every expired session. A session that fails to stop does not block the others. Returns how many ended.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow() - options.EffectiveMaxSessionAge;
        var ended = 0;
        List<Exception>? errors = null;
        foreach (var session in launcher.AllActiveSessions().Where(s => s.StartedAt <= cutoff))
        {
            try
            {
                await launcher.EndAsync(session.SessionId, ct).ConfigureAwait(false);
                ended++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
            throw new AggregateException("Some expired browser sessions did not stop cleanly.", errors);
        return ended;
    }
}
