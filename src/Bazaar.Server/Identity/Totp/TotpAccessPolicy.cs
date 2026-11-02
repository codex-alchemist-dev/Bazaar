using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Identity.Totp;

/// <summary>
/// Maps a service to the approval tier for its TOTP codes (doc 14, "TOTP"). Services in the operator's sensitive list
/// use <see cref="ToolTier"/> from <see cref="IdentityOptions.TotpTierSensitive"/>; all others use
/// <see cref="IdentityOptions.TotpTierOrdinary"/>. Both tiers are checked against <see cref="IdentityOptions.TotpFloor"/>.
/// </summary>
public sealed class TotpAccessPolicy
{
    private readonly HashSet<string> _sensitiveServices;

    /// <summary>Creates a policy from explicit tiers and the sensitive service list.</summary>
    /// <param name="ordinaryTier">Tier for services not on the sensitive list. Must not be below the TOTP floor.</param>
    /// <param name="sensitiveTier">Tier for sensitive services. Must not be below the TOTP floor.</param>
    /// <param name="sensitiveServices">Service ids that count as sensitive (money or identity). Matched case-insensitively.</param>
    /// <exception cref="ArgumentOutOfRangeException">A tier is undefined or below <see cref="IdentityOptions.TotpFloor"/>.</exception>
    /// <exception cref="ArgumentException">A sensitive service id is null, empty or whitespace.</exception>
    public TotpAccessPolicy(ToolTier ordinaryTier, ToolTier sensitiveTier, IEnumerable<string> sensitiveServices)
    {
        ArgumentNullException.ThrowIfNull(sensitiveServices);
        ValidateTier(ordinaryTier, nameof(ordinaryTier));
        ValidateTier(sensitiveTier, nameof(sensitiveTier));

        OrdinaryTier = ordinaryTier;
        SensitiveTier = sensitiveTier;

        _sensitiveServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string service in sensitiveServices)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(service);
            _sensitiveServices.Add(service);
        }
    }

    /// <summary>Tier applied to services that are not sensitive.</summary>
    public ToolTier OrdinaryTier { get; }

    /// <summary>Tier applied to sensitive services.</summary>
    public ToolTier SensitiveTier { get; }

    /// <summary>Builds a policy from the identity settings and the operator's sensitive service list.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A tier in <paramref name="options"/> is below the TOTP floor.</exception>
    public static TotpAccessPolicy From(IdentityOptions options, IEnumerable<string> sensitiveServices)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TotpAccessPolicy(options.TotpTierOrdinary, options.TotpTierSensitive, sensitiveServices);
    }

    /// <summary>True when the service is on the sensitive list.</summary>
    public bool IsSensitive(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        return _sensitiveServices.Contains(service);
    }

    /// <summary>The approval tier that applies to TOTP codes for <paramref name="service"/>.</summary>
    public ToolTier TierFor(string service) => IsSensitive(service) ? SensitiveTier : OrdinaryTier;

    private static void ValidateTier(ToolTier tier, string paramName)
    {
        if (!Enum.IsDefined(tier))
        {
            throw new ArgumentOutOfRangeException(paramName, tier, "Unknown approval tier.");
        }

        if (tier < IdentityOptions.TotpFloor)
        {
            throw new ArgumentOutOfRangeException(paramName, tier, $"TOTP tiers may not go below {IdentityOptions.TotpFloor}.");
        }
    }
}
