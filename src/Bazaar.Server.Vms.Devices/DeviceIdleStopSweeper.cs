using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Vms.Devices;

/// <summary>
/// One pass of the device-guest idle stop (doc 62 section 9.3). Reads <c>vms.devices.idle_stop_minutes</c> on every pass (0 disables),
/// then stops, gracefully and through the lifecycle, each running device guest that is not busy and whose last use is at least that
/// long ago. A busy guest (exec, terminal or browser session) and a pair VM are never stopped. Each stop is logged and audited as
/// <see cref="AuditKind"/> with the VM id, the provider kind and the idle minutes only. A refused or failed stop is logged and tried
/// again on the next pass.
/// </summary>
public sealed partial class DeviceIdleStopSweeper(
    VmLifecycleService lifecycle,
    IDeviceGuestPolicy policy,
    TimeProvider clock,
    ILogger<DeviceIdleStopSweeper> logger,
    IAuditLog? audit = null)
{
    /// <summary>The audit kind of an idle stop.</summary>
    public const string AuditKind = "vms.devices.idle_stop";

    /// <summary>The audit actor of an idle stop.</summary>
    public const string Actor = "vms-devices";

    /// <summary>Runs one pass and returns how many guests it stopped.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        int minutes = await policy.GetIdleStopMinutesAsync(ct).ConfigureAwait(false);
        if (minutes <= 0)
            return 0;

        var limit = TimeSpan.FromMinutes(minutes);
        var now = clock.GetUtcNow();
        int stopped = 0;
        foreach (var guest in await lifecycle.ListRunningAsync(ct).ConfigureAwait(false))
        {
            var idle = now - guest.Activity.LastUsedAt;
            if (!guest.IsDeviceGuest || guest.Activity.IsBusy || idle < limit)
                continue;

            VmOperationResult result;
            try
            {
                result = await lifecycle.StopAsync(guest.Vm.Id, StopMode.Graceful, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogStopFailed(logger, guest.Vm.Id, ex);
                continue;
            }

            if (result is VmOperationResult.Refused refused)
            {
                LogStopRefused(logger, guest.Vm.Id, refused.Refusal.Reason);
                continue;
            }

            stopped++;
            int idleMinutes = (int)Math.Floor(idle.TotalMinutes);
            LogStopped(logger, guest.Vm.Id, guest.Kind ?? "unknown", idleMinutes);
            await AuditAsync(guest, idleMinutes, now).ConfigureAwait(false);
        }

        return stopped;
    }

    private async Task AuditAsync(RunningVmInfo guest, int idleMinutes, DateTimeOffset at)
    {
        if (audit is null)
            return;

        var payload = new JsonObject
        {
            ["vm_id"] = guest.Vm.Id.ToString(),
            ["provider_kind"] = guest.Kind,
            ["idle_minutes"] = idleMinutes,
        };
        try
        {
            // The guest is already stopped, so the entry is written even if the pass is being cancelled.
            await audit.AppendAsync(new AuditEntry(at, Actor, AuditKind, payload.ToJsonString()), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAuditFailed(logger, guest.Vm.Id, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped idle device guest {VmId} ({Kind}) after {IdleMinutes} minutes.")]
    private static partial void LogStopped(ILogger logger, Guid vmId, string kind, int idleMinutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The idle stop of device guest {VmId} was refused ({Reason}); it is tried again on the next pass.")]
    private static partial void LogStopRefused(ILogger logger, Guid vmId, VmRefusalReason reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The idle stop of device guest {VmId} failed; it is tried again on the next pass.")]
    private static partial void LogStopFailed(ILogger logger, Guid vmId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "The audit entry for the idle stop of device guest {VmId} could not be written.")]
    private static partial void LogAuditFailed(ILogger logger, Guid vmId, Exception ex);
}

/// <summary>
/// Runs <see cref="DeviceIdleStopSweeper.RunOnceAsync"/> every <see cref="Interval"/> (doc 62 section 9.3). The lifecycle is resolved
/// when the service starts; a host without one registers no VMs, and the service then ends at once.
/// </summary>
public sealed partial class DeviceIdleStopService(
    IServiceProvider services,
    TimeProvider clock,
    ILogger<DeviceIdleStopService> logger) : BackgroundService
{
    /// <summary>How often a pass runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService<VmLifecycleService>() is not { } lifecycle)
            return;
        var sweeper = new DeviceIdleStopSweeper(
            lifecycle,
            services.GetRequiredService<IDeviceGuestPolicy>(),
            clock,
            services.GetRequiredService<ILogger<DeviceIdleStopSweeper>>(),
            services.GetService<IAuditLog>());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, clock, stoppingToken).ConfigureAwait(false);
                await sweeper.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed pass (for example, the store is down) is retried at the next interval.
                LogPassFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Device idle-stop pass failed.")]
    private static partial void LogPassFailed(ILogger logger, Exception ex);
}
