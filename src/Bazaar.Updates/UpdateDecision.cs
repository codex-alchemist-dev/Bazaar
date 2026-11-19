namespace Bazaar.Updates;

/// <summary>Why an offered update is refused. Refusals are always explicit, never silent.</summary>
public enum UpdateRefusal
{
    /// <summary>The offered version is not newer than the installed one (downgrades and re-installs are refused).</summary>
    NotNewer,

    /// <summary>The offered version string is not a valid Bazaar version.</summary>
    InvalidVersion,

    /// <summary>The package URL is neither HTTPS nor loopback HTTP.</summary>
    InsecureUrl,
}

/// <summary>The result of deciding whether to take an offered update.</summary>
/// <param name="Apply">True when the update should be downloaded and verified.</param>
/// <param name="Refusal">Why it was refused, or null when it is applied.</param>
/// <param name="Offered">The parsed offered version, or null when the string was invalid.</param>
public readonly record struct UpdateDecision(bool Apply, UpdateRefusal? Refusal, UpdateVersion? Offered)
{
    /// <summary>Decides whether an offered manifest should be downloaded and installed.</summary>
    public static UpdateDecision Decide(UpdateVersion installed, UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!UpdateVersion.TryParse(manifest.Version, out var offered))
        {
            return new UpdateDecision(false, UpdateRefusal.InvalidVersion, null);
        }

        if (offered <= installed)
        {
            return new UpdateDecision(false, UpdateRefusal.NotNewer, offered);
        }

        if (!IsSecureTransport(manifest.PackageUrl))
        {
            return new UpdateDecision(false, UpdateRefusal.InsecureUrl, offered);
        }

        return new UpdateDecision(true, null, offered);
    }

    /// <summary>HTTPS everywhere, plus plain HTTP only on loopback (for local testing).</summary>
    public static bool IsSecureTransport(Uri? url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback));
}
