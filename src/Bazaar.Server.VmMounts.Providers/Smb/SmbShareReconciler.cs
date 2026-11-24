using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmMounts.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.VmMounts.Providers.Smb;

/// <summary>What one reconcile pass did.</summary>
/// <param name="Removed">Stale shares removed (prefixed, strict-pattern names with no attached mount row).</param>
/// <param name="Kept">Prefixed shares that belong to an attached mount.</param>
/// <param name="Ignored">Shares that start with the prefix but do not match the strict pattern. They were not created by this module and are left alone.</param>
public sealed record SmbReconcileResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Kept, IReadOnlyList<string> Ignored);

/// <summary>
/// Removes SMB shares this module created for mounts that are no longer attached (a crash after the row was marked detached, a detach
/// that failed, a restore of an older host). The rule is narrow on purpose: only a share whose name matches the strict pattern
/// <c>bazaar-&lt;12 hex&gt;-(soul|work)-&lt;32 hex&gt;</c> and whose name is not the expected name of any attached mount row is removed.
/// </summary>
/// <remarks>
/// Shares are listed before the rows are read. A mount row is inserted before its share is created, so every listed share that belongs to
/// a live attach already has its row by the time the rows are read; the other order could remove a share that was just created.
/// </remarks>
public sealed partial class SmbShareReconciler
{
    private readonly SmbMountProvider _provider;
    private readonly IScopedMountStore _store;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<SmbShareReconciler> _logger;

    public SmbShareReconciler(
        SmbMountProvider provider, IScopedMountStore store, IAuditLog audit, TimeProvider time, ILogger<SmbShareReconciler> logger)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _provider = provider;
        _store = store;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    public async Task<SmbReconcileResult> ReconcileAsync(CancellationToken ct)
    {
        var listed = await _provider.ListPrefixedSharesAsync(ct).ConfigureAwait(false);
        var rows = await _store.ListAttachedAsync(ct).ConfigureAwait(false);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            expected.Add(SmbShareNames.For(row.AgentId, row.Kind, row.Id));

        var removed = new List<string>();
        var kept = new List<string>();
        var ignored = new List<string>();
        foreach (var name in listed)
        {
            if (!SmbShareNames.IsValidShare(name))
            {
                ignored.Add(Clean(name));
                continue;
            }

            if (expected.Contains(name))
            {
                kept.Add(name);
                continue;
            }

            await _provider.RemoveShareByNameAsync(name, ct).ConfigureAwait(false);
            removed.Add(name);
            LogRemoved(_logger, name);
            await _audit.AppendAsync(
                new AuditEntry(_time.GetUtcNow(), VmMountAuditActions.Actor, MountProviderAuditActions.StaleShareRemoved, new JsonObject
                {
                    ["share"] = name,
                    ["reason"] = "no attached mount row",
                }.ToJsonString()),
                ct).ConfigureAwait(false);
        }

        return new SmbReconcileResult(removed, kept, ignored);
    }

    private static string Clean(string value) =>
        new([.. value.Where(c => c is >= ' ' and <= '~').Take(80)]);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removed stale SMB share {Share}: no attached mount row.")]
    private static partial void LogRemoved(ILogger logger, string share);
}

/// <summary>
/// Runs <see cref="SmbShareReconciler"/> at start, and again after a failure, with growing waits, until it succeeds. It does nothing when
/// the mounts module is not composed or the mount provider is not the SMB one. A failure is logged as an error each time and never ends
/// the host: a stale share stays protected by its ACL, and the next pass retries.
/// </summary>
internal sealed partial class SmbShareReconcileService(IServiceProvider services, TimeProvider time, ILogger<SmbShareReconcileService> logger)
    : BackgroundService
{
    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Resolve nothing that needs the mounts module until we know it is there.
        if (services.GetService<VmMountOptions>() is null || services.GetService<IScopedMountStore>() is null)
            return;
        if (services.GetService<IMountProvider>() is not SmbMountProvider smb)
            return;

        var wait = FirstRetry;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reconciler = new SmbShareReconciler(
                    smb,
                    services.GetRequiredService<IScopedMountStore>(),
                    services.GetRequiredService<IAuditLog>(),
                    time,
                    services.GetRequiredService<ILogger<SmbShareReconciler>>());
                var result = await reconciler.ReconcileAsync(stoppingToken).ConfigureAwait(false);
                LogDone(logger, result.Removed.Count, result.Kept.Count, result.Ignored.Count);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex, wait);
            }

            try
            {
                await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            wait = TimeSpan.FromTicks(Math.Min(MaxRetry.Ticks, wait.Ticks * 2));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SMB reconcile done: {Removed} stale removed, {Kept} kept, {Ignored} ignored.")]
    private static partial void LogDone(ILogger logger, int removed, int kept, int ignored);

    [LoggerMessage(Level = LogLevel.Error, Message = "SMB reconcile failed; stale shares may remain. Retrying in {Wait}.")]
    private static partial void LogFailed(ILogger logger, Exception ex, TimeSpan wait);
}
