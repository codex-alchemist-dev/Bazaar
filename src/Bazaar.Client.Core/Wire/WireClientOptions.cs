using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Wire;

/// <summary>Settings for <see cref="WireClient"/>.</summary>
public sealed class WireClientOptions
{
    /// <summary>Stable device id sent in <c>hello</c>.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Device class, such as <c>windows</c>.</summary>
    public required string DeviceClass { get; init; }

    /// <summary>Client build string.</summary>
    public required string ClientBuild { get; init; }

    /// <summary>Credential sent in <c>hello</c>, or null when it rides in the upgrade header.</summary>
    public string? Credential { get; init; }

    /// <summary>Protocol major this client speaks.</summary>
    public int ProtocolMajor { get; init; } = 1;

    /// <summary>Protocol minor this client implements.</summary>
    public int ProtocolMinor { get; init; }

    /// <summary>First reconnect delay.</summary>
    public TimeSpan BackoffStart { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest reconnect delay (<c>wire.reconnect_backoff_max_s</c>).</summary>
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Source of jitter in [0, 1). Tests pass a fixed value.</summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;
}

/// <summary>Why the client stopped for good.</summary>
public enum WireClientStopReason
{
    /// <summary>The caller cancelled.</summary>
    Cancelled,

    /// <summary>No protocol version overlaps; the user must update the app.</summary>
    UpgradeRequired,

    /// <summary>The credential was refused or expired.</summary>
    AuthFailed,

    /// <summary>The device or session was revoked.</summary>
    Revoked,
}

/// <summary>A request failed with an <c>err</c> envelope.</summary>
public sealed class WireRequestException(WireError error) : Exception($"{error.Code}: {error.Message}")
{
    /// <summary>The server's error.</summary>
    public WireError Error { get; } = error;
}
