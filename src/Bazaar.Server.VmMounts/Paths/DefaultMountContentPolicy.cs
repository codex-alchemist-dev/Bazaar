using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmMounts.Paths;

/// <summary>
/// The conservative stand-in for the content policy: it refuses key material by name (private keys, key stores, SSH and cloud
/// credential folders, password stores). The real allow-list lives in <c>Bazaar.Server.Identity.Mount</c>; a follow-up adapts it to
/// <see cref="IMountContentPolicy"/> and replaces this default. Canaries are always allowed here.
/// </summary>
public sealed class DefaultMountContentPolicy : IMountContentPolicy
{
    private static readonly HashSet<string> _deniedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", "secrets", "audit",
        ".netrc", ".pgpass", ".git-credentials", ".npmrc", ".env", "credentials", "credentials.json",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
    };

    private static readonly string[] _deniedExtensions =
    [
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".kdbx", ".ppk", ".gpg", ".pgp",
    ];

    public MountContentDecision Evaluate(MountContentCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Origin == MountContentOrigin.Canary)
            return MountContentDecision.Allow;

        foreach (var segment in candidate.RelativePath.Split('/', '\\'))
        {
            if (_deniedNames.Contains(segment))
                return MountContentDecision.Refuse($"'{segment}' is key material or a credential store");
            if (segment.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("id_ecdsa", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("id_dsa", StringComparison.OrdinalIgnoreCase))
                return MountContentDecision.Refuse($"'{segment}' looks like an SSH private key");
            foreach (var ext in _deniedExtensions)
            {
                if (segment.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return MountContentDecision.Refuse($"'{segment}' has a key-material extension");
            }
        }

        return MountContentDecision.Allow;
    }
}
