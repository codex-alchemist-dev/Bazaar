using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Wire.Discovery;

/// <summary>The three operator values of <see cref="WireDiscoverySettings"/> as one reading.</summary>
public sealed record WireDiscoverySettingValues(bool Enabled, int PermitLimit, int WindowSeconds);

/// <summary>
/// Reads the current values from the settings store. The host implements it over <c>ISettingStore</c>, because the store lives in
/// Bazaar.Server and a feature module must not reference it.
/// </summary>
public interface IWireDiscoverySettingsReader
{
    /// <summary>The effective values (stored or default).</summary>
    Task<WireDiscoverySettingValues> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The latest operator values, held in memory. The route reads <see cref="Current"/> on every request, which is a volatile field read:
/// the anonymous route never waits on the database. <see cref="WireDiscoverySettingsRefresher"/> keeps it fresh. Until a reading
/// succeeds there is no overlay and <see cref="WireDiscoveryOptions"/> alone applies.
/// </summary>
public sealed class WireDiscoveryLiveSettings
{
    private volatile WireDiscoverySettingValues? _current;

    /// <summary>The last successful reading, or null before the first one.</summary>
    public WireDiscoverySettingValues? Current => _current;

    /// <summary>Stores a reading.</summary>
    public void Update(WireDiscoverySettingValues values) => _current = values;

    /// <summary>Returns <paramref name="baseline"/> with the live operator values laid over it.</summary>
    internal WireDiscoveryOptions Overlay(WireDiscoveryOptions baseline)
    {
        var live = _current;
        if (live is null)
        {
            return baseline;
        }

        return new WireDiscoveryOptions
        {
            Enabled = live.Enabled,
            BasePath = baseline.BasePath,
            PermitLimit = live.PermitLimit,
            Window = TimeSpan.FromSeconds(live.WindowSeconds),
            MaxTrackedClients = baseline.MaxTrackedClients,
            CacheMaxAgeSeconds = baseline.CacheMaxAgeSeconds,
        };
    }
}

/// <summary>Polls the settings reader and publishes into <see cref="WireDiscoveryLiveSettings"/>. A failed read keeps the last good values.</summary>
public sealed partial class WireDiscoverySettingsRefresher(
    IWireDiscoverySettingsReader reader,
    WireDiscoveryLiveSettings live,
    IOptions<WireDiscoveryOptions> options,
    ILogger<WireDiscoverySettingsRefresher> logger,
    TimeProvider time) : BackgroundService
{
    /// <summary>Reads once and publishes. Public so a test can drive a refresh without waiting.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            live.Update(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRefreshFailed(ex);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken).ConfigureAwait(false);
        var interval = options.Value.SettingsRefreshInterval;
        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the wire discovery settings; keeping the last values.")]
    private partial void LogRefreshFailed(Exception exception);
}
