using Bazaar.Server.Identity;

namespace Bazaar.Server.Identity.Mount;

/// <summary>
/// The fixed rule for what may be written into a mounted folder (docs/design/14-identity-service-and-passport.md,
/// "Mounted folder"). This is a hard floor in code, not a setting: only the session-cookie profile may carry a
/// credential, and metadata and canaries carry none. Every other credential kind is refused.
/// </summary>
public static class MountAllowList
{
    /// <summary>Whether a credential of this kind may be written into the mount. Unlisted kinds are refused.</summary>
    public static bool IsPermitted(CredentialKind kind) => kind switch
    {
        CredentialKind.Cookie => true,

        // ProviderKey, AppKey, Owner, OAuth, Ssh, Totp and Recovery, and any kind added later, stay out of the mount.
        _ => false,
    };

    /// <summary>Throws <see cref="ArgumentException"/> when a credential of this kind may not be mounted.</summary>
    public static void EnsureCredentialPermitted(CredentialKind kind)
    {
        if (!IsPermitted(kind))
        {
            throw new ArgumentException($"Credential kind '{kind}' may never be written into the mounted folder.", nameof(kind));
        }
    }

    /// <summary>
    /// Validates one entry: its kind and credential class must match the fixed rule, and its file name must be safe.
    /// Throws <see cref="ArgumentException"/> when the entry is refused.
    /// </summary>
    public static void EnsureEntryAllowed(MountEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        switch (entry.Kind)
        {
            case MountedFileKind.Metadata:
            case MountedFileKind.Canary:
                if (entry.Credential is not null)
                {
                    throw new ArgumentException($"{entry.Kind} entries carry no credential class.", nameof(entry));
                }

                break;

            case MountedFileKind.CookieProfile:
                if (entry.Credential is not { } kind)
                {
                    throw new ArgumentException("A cookie profile entry must name its credential kind.", nameof(entry));
                }

                EnsureCredentialPermitted(kind);
                break;

            default:
                throw new ArgumentException($"Unknown mounted file kind '{entry.Kind}'.", nameof(entry));
        }

        MountNameRules.EnsureSafeFileName(entry.FileName);
    }
}
