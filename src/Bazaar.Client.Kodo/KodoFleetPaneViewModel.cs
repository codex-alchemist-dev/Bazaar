using System.Globalization;

namespace Bazaar.Client.Kodo;

/// <summary>A client device's stream state as the server reports it (doc 46 section 14.2, view 7).</summary>
public enum KodoFleetStreamState
{
    Connected,
    Disconnected,
    Unobserved,
    Unknown,
}

/// <summary>Where a device's version stands against its channel.</summary>
public enum KodoVersionStanding
{
    Current,
    Behind,
    Ahead,
    Unknown,
}

/// <summary>
/// One fleet device as the read API <c>GET /fleet</c> will send it (doc 46 section 15.1). There is no fleet DTO in
/// <c>Bazaar.Contracts</c> yet, so this input record is a PROPOSED seam (doc 46 section 14.6); it moves to Contracts with the
/// server fleet endpoint. <paramref name="StreamState"/> uses the wire words <c>connected</c>, <c>disconnected</c> and
/// <c>unobserved</c>; anything else shows as unknown.
/// </summary>
/// <param name="CrashesPer100Sessions">The crash rate the server computed, or null when there is no data.</param>
public sealed record KodoFleetDeviceInput(
    string DeviceId,
    string DeviceName,
    string Version,
    string ChannelVersion,
    DateTimeOffset? LastSeen,
    double? CrashesPer100Sessions,
    string StreamState);

/// <summary>How the Fleet view orders its rows.</summary>
public enum KodoFleetSort
{
    /// <summary>Worst first: stream state, then crash rate, then behind on version, then oldest last seen, then name.</summary>
    Attention,
    Name,
    LastSeen,
    CrashRate,
}

/// <summary>One Fleet row, all text ready to show.</summary>
public sealed record KodoFleetRow(
    string DeviceId,
    string DeviceName,
    string VersionText,
    KodoVersionStanding Standing,
    string LastSeenText,
    bool IsStale,
    string CrashRateText,
    KodoFleetStreamState StreamState,
    string StreamText,
    string AccessibleName);

/// <summary>The Fleet view's display model.</summary>
public sealed record KodoFleetPanel(bool Permitted, string EmptyText, KodoFleetSort Sort, IReadOnlyList<KodoFleetRow> Rows);

/// <summary>
/// Pure view model of the Fleet view (doc 46 section 14.2, view 7). Sorting is deterministic: every ordering ends in the
/// device name and then the device id, ordinal, so equal rows never swap between refreshes.
/// </summary>
public sealed class KodoFleetPaneViewModel(TimeProvider time, IKodoPermissions permissions)
{
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly IKodoPermissions _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
    private IReadOnlyList<KodoFleetDeviceInput> _devices = [];

    /// <summary>The active ordering.</summary>
    public KodoFleetSort Sort { get; set; } = KodoFleetSort.Attention;

    /// <summary>Replaces the device list with the latest one from the read API or the <c>fleet</c> topic.</summary>
    public void SetDevices(IEnumerable<KodoFleetDeviceInput> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices.ToList();
    }

    /// <summary>Builds the panel at the current time.</summary>
    public KodoFleetPanel Build()
    {
        if (!_permissions.Can(KodoPermissions.ViewFleet))
        {
            return new KodoFleetPanel(false, "Fleet is not available for your role.", Sort, []);
        }

        var now = _time.GetUtcNow();
        var rows = _devices.Select(d => ToRow(d, now)).ToList();
        var ordered = Order(rows, _devices.ToDictionary(d => d.DeviceId, StringComparer.Ordinal), now).ToList();
        return new KodoFleetPanel(true, ordered.Count == 0 ? "No devices reported." : string.Empty, Sort, ordered);
    }

    private IEnumerable<KodoFleetRow> Order(List<KodoFleetRow> rows, Dictionary<string, KodoFleetDeviceInput> byId, DateTimeOffset now)
    {
        IOrderedEnumerable<KodoFleetRow> ordered = Sort switch
        {
            KodoFleetSort.Name => rows.OrderBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase),
            KodoFleetSort.LastSeen => rows.OrderBy(r => byId[r.DeviceId].LastSeen is null ? 0 : 1).ThenBy(r => byId[r.DeviceId].LastSeen),
            KodoFleetSort.CrashRate => rows.OrderByDescending(r => byId[r.DeviceId].CrashesPer100Sessions ?? -1),
            _ => rows
                .OrderBy(r => StreamRank(r.StreamState))
                .ThenByDescending(r => byId[r.DeviceId].CrashesPer100Sessions ?? -1)
                .ThenBy(r => r.Standing == KodoVersionStanding.Behind ? 0 : 1)
                .ThenBy(r => byId[r.DeviceId].LastSeen is null ? 0 : 1)
                .ThenBy(r => byId[r.DeviceId].LastSeen),
        };

        return ordered
            .ThenBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.DeviceName, StringComparer.Ordinal)
            .ThenBy(r => r.DeviceId, StringComparer.Ordinal);
    }

    private static int StreamRank(KodoFleetStreamState state) => state switch
    {
        KodoFleetStreamState.Unobserved => 0,
        KodoFleetStreamState.Disconnected => 1,
        KodoFleetStreamState.Unknown => 2,
        _ => 3,
    };

    private static KodoFleetRow ToRow(KodoFleetDeviceInput d, DateTimeOffset now)
    {
        var standing = Standing(d.Version, d.ChannelVersion);
        var versionText = standing switch
        {
            KodoVersionStanding.Current => $"{d.Version} (current for channel)",
            KodoVersionStanding.Behind => $"{d.Version} (behind {d.ChannelVersion})",
            KodoVersionStanding.Ahead => $"{d.Version} (ahead of {d.ChannelVersion})",
            _ => $"{d.Version} (channel {(string.IsNullOrWhiteSpace(d.ChannelVersion) ? "unknown" : d.ChannelVersion)})",
        };

        var stale = false;
        string seen;
        if (d.LastSeen is { } at)
        {
            var age = now - at;
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }

            stale = age > KodoStatusViewModel.StaleAfter;
            seen = $"Last seen {KodoStatusViewModel.FormatAge(age)} ago";
        }
        else
        {
            seen = "Never seen";
            stale = true;
        }

        var crash = d.CrashesPer100Sessions is { } rate && rate >= 0 && double.IsFinite(rate)
            ? $"{rate.ToString("0.#", CultureInfo.InvariantCulture)} crashes per 100 sessions"
            : "Crash rate not reported";
        var stream = d.StreamState switch
        {
            "connected" => KodoFleetStreamState.Connected,
            "disconnected" => KodoFleetStreamState.Disconnected,
            "unobserved" => KodoFleetStreamState.Unobserved,
            _ => KodoFleetStreamState.Unknown,
        };
        var streamText = stream switch
        {
            KodoFleetStreamState.Connected => "Stream connected",
            KodoFleetStreamState.Disconnected => "Stream disconnected",
            KodoFleetStreamState.Unobserved => "Stream unobserved",
            _ => "Stream state unknown",
        };

        return new KodoFleetRow(
            d.DeviceId, d.DeviceName, versionText, standing, seen, stale, crash, stream, streamText,
            $"{d.DeviceName}: {versionText}. {seen}. {crash}. {streamText}.");
    }

    /// <summary>
    /// Compares versions like <c>0.1.0+a1b2c3d</c> (doc VERSIONING): build metadata after <c>+</c> is ignored, the numeric core
    /// decides, and a release outranks a pre-release of the same core. Anything unreadable is unknown, never guessed.
    /// </summary>
    public static KodoVersionStanding Standing(string version, string channel)
    {
        if (!TryParse(version, out var v, out var vPre) || !TryParse(channel, out var c, out var cPre))
        {
            return KodoVersionStanding.Unknown;
        }

        var core = v.CompareTo(c);
        if (core == 0)
        {
            core = (vPre, cPre) switch
            {
                (null, null) => 0,
                (null, _) => 1,
                (_, null) => -1,
                _ => string.CompareOrdinal(vPre, cPre),
            };
        }

        return core < 0 ? KodoVersionStanding.Behind : core > 0 ? KodoVersionStanding.Ahead : KodoVersionStanding.Current;
    }

    private static bool TryParse(string? text, out Version core, out string? pre)
    {
        core = new Version(0, 0);
        pre = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim().TrimStart('v');
        var plus = span.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            span = span[..plus];
        }

        var dash = span.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = span[(dash + 1)..];
            span = span[..dash];
        }

        if (!Version.TryParse(span, out var parsed) || parsed is null)
        {
            return false;
        }

        core = parsed;
        return true;
    }
}
