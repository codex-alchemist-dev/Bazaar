using Bazaar.Server.Athanor;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// <see cref="IAthanorLimits"/> backed by the settings registry (<c>athanor.max_open_bytes</c>, <c>athanor.max_save_bytes</c>,
/// <c>athanor.save_rate_per_min</c>; install scope, operator-changed, minimum 1, safe defaults from <see cref="AthanorLimits"/>).
/// The properties are read synchronously by the handlers, so the host calls <see cref="RefreshAsync"/> before each request; a change
/// takes effect on the next call. If a read fails (an invalid stored value), the last good values stay in force, never a loosened set.
/// </summary>
public sealed class SettingsAthanorLimits(ISettingStore settings) : IAthanorLimits
{
    private long _maxOpen = AthanorLimits.DefaultMaxBytes;
    private long _maxSave = AthanorLimits.DefaultMaxBytes;
    private int _rate = AthanorLimits.DefaultSaveRatePerMinute;

    /// <inheritdoc />
    public long MaxOpenBytes => Volatile.Read(ref _maxOpen);

    /// <inheritdoc />
    public long MaxSaveBytes => Volatile.Read(ref _maxSave);

    /// <inheritdoc />
    public int SaveRatePerMinute => Volatile.Read(ref _rate);

    /// <summary>Reads the three settings and keeps them for the next calls.</summary>
    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var open = await settings.GetAsync(AthanorSettings.MaxOpenBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
            var save = await settings.GetAsync(AthanorSettings.MaxSaveBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
            var rate = await settings.GetAsync(AthanorSettings.SaveRatePerMinute, cancellationToken: cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _maxOpen, Math.Max(1, open));
            Volatile.Write(ref _maxSave, Math.Max(1, save));
            Volatile.Write(ref _rate, Math.Max(1, rate));
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            // Keep the last good values.
        }
    }
}
