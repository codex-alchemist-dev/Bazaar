using Bazaar.Server.Settings;

namespace Bazaar.Server.Wire.Discovery;

/// <summary>
/// The discovery route's operator settings (doc 90 section 8, registered in doc 03's registry). All install scope, operator-set,
/// audited by the settings store. Turning the route off is a tightening and needs no approval. The rate keys may be lowered or
/// raised only inside fixed bounds: the limiter can never be switched off, which is the floor (CLAUDE.md configurability rule).
/// </summary>
public static class WireDiscoverySettings
{
    /// <summary>Largest permit count the operator may set. Above this the limiter would stop protecting the anonymous route.</summary>
    public const int MaxPermitLimit = 6_000;

    /// <summary>Longest window the operator may set, in seconds.</summary>
    public const int MaxWindowSeconds = 3_600;

    /// <summary>Whether the route answers. Off gives 404 on both routes and clients rely on <c>welcome</c>.</summary>
    public static readonly SettingKey<bool> Enabled = new(
        "wire.discovery.enabled", true, SettingScope.Install, SettingRole.Operator,
        "Serve the wire discovery route (/.well-known/bazaar/wire). Off answers 404 and clients rely on the welcome message.");

    public static readonly SettingKey<int> PermitLimit = new(
        "wire.discovery.rate.permit_limit", new WireDiscoveryOptions().PermitLimit, SettingScope.Install, SettingRole.Operator,
        "Requests the discovery route allows per client address and window across both routes.",
        validate: v => v is >= 1 and <= MaxPermitLimit ? null : $"'wire.discovery.rate.permit_limit' must be between 1 and {MaxPermitLimit}; the rate limit cannot be switched off.");

    public static readonly SettingKey<int> WindowSeconds = new(
        "wire.discovery.rate.window_seconds", (int)new WireDiscoveryOptions().Window.TotalSeconds, SettingScope.Install, SettingRole.Operator,
        "Length of the discovery rate-limit window, in seconds.",
        validate: v => v is >= 1 and <= MaxWindowSeconds ? null : $"'wire.discovery.rate.window_seconds' must be between 1 and {MaxWindowSeconds}.");

    /// <summary>Every discovery key.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } = [Enabled, PermitLimit, WindowSeconds];
}

/// <summary>The discovery module's contribution to the settings registry.</summary>
public sealed class WireDiscoverySettingContribution : ISettingContribution
{
    /// <inheritdoc />
    public IReadOnlyList<SettingDefinition> Settings => WireDiscoverySettings.All;
}
