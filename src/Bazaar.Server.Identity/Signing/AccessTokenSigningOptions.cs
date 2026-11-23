namespace Bazaar.Server.Identity.Signing;

/// <summary>
/// Access-token signing settings. Defaults are the documented values. A retired key verifies until its retirement plus
/// <see cref="AccessTokenLifetime"/> plus <see cref="ClockSkew"/>.
/// </summary>
public sealed record AccessTokenSigningOptions
{
    /// <summary>A new live key is created when the current one is this many days old, checked at issue time.</summary>
    public int SigningKeyRotationDays { get; init; } = 30;

    /// <summary>The access token lifetime. Also the grace period a retired key keeps verifying for.</summary>
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Extra time added to the grace period for clock skew between hosts.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a retired key verifies after it is retired: lifetime plus skew.</summary>
    public TimeSpan Grace => AccessTokenLifetime + ClockSkew;
}
