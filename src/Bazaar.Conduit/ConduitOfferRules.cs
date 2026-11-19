namespace Bazaar.Conduit;

/// <summary>Checks that an offer respects the shipped floors of doc 39 section 3.</summary>
public static class ConduitOfferRules
{
    /// <summary>
    /// Returns null when the offer is acceptable, or the reason it must not be published. Rules: plain
    /// <c>ws://</c> only on loopback, <c>wss://</c> everywhere else, a pinned key hash, and no credentials in the URI.
    /// </summary>
    public static string? Validate(ConduitOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (string.IsNullOrWhiteSpace(offer.ModuleId))
        {
            return "module id is empty";
        }

        if (!offer.Uri.IsAbsoluteUri)
        {
            return "uri is not absolute";
        }

        var isPlain = string.Equals(offer.Uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase);
        var isSecure = string.Equals(offer.Uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);

        if (!isPlain && !isSecure)
        {
            return $"scheme '{offer.Uri.Scheme}' is not ws or wss";
        }

        if (isPlain && offer.Scope != ConduitScope.Loopback)
        {
            return "plain ws is allowed only on loopback";
        }

        if (string.IsNullOrEmpty(offer.ServerKeyHash) || !offer.ServerKeyHash.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return "server key hash is missing or not sha256";
        }

        if (!string.IsNullOrEmpty(offer.Uri.UserInfo)
            || (!string.IsNullOrEmpty(offer.Uri.Query) && offer.Uri.Query.Contains("token", StringComparison.OrdinalIgnoreCase)))
        {
            return "uri carries credentials";
        }

        return null;
    }
}
