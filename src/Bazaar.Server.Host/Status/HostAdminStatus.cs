using Bazaar.Contracts;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Host.Status;

/// <summary>
/// The doc 51 status port: the same facts <c>GET /status</c> reports (version, codename, start time, uptime, one line per provider),
/// read from the host's own state. The host owns these facts, so the port lives here and the panel and the resident TUI both read it.
/// </summary>
public sealed class HostAdminStatusSource(HostState state, ProviderCapabilityReport providers) : IAdminStatusSource
{
    public const string DisplayName = "Bazaar server";

    public Task<AdminStatusView> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AdminStatusView(
            DisplayName,
            ProductVersion.Informational,
            ProductVersion.CurrentCodename,
            state.StartedAt,
            (long)state.Uptime.TotalSeconds,
            providers.Lines));
    }
}

/// <summary>
/// Writes the status view to <see cref="AdminStatusFile"/> every 15 seconds so the resident TUI, a separate process, can show it
/// (doc 58 section 3). A write failure is logged once and retried at the next tick; it never stops the host.
/// </summary>
public sealed partial class AdminStatusFileService(
    IAdminStatusSource source,
    string path,
    TimeProvider time,
    ILogger<AdminStatusFileService> logger) : BackgroundService
{
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        var failing = false;
        try
        {
            do
            {
                failing = await WriteOnceAsync(failing, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    /// <summary>One write. Returns whether the write is now failing, so the caller logs a streak once.</summary>
    public async Task<bool> WriteOnceAsync(bool wasFailing, CancellationToken ct)
    {
        try
        {
            var view = await source.GetAsync(ct).ConfigureAwait(false);
            AdminStatusFile.Write(path, view, time.GetUtcNow());
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!wasFailing)
            {
                LogWriteFailed(logger, error);
            }

            return true;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server status file could not be written; the resident TUI will show the server as not reporting.")]
    private static partial void LogWriteFailed(ILogger logger, Exception error);
}
