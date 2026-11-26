using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.GitHub.Ingress;

/// <summary>
/// The GitHub bridge system principal (doc 21, Ingress). Bazaar has no principal table to seed: system authors are fixed ids that
/// GC rows carry in <c>author_id</c> under <c>AuthorKind.System</c> or <c>ExternalBridge</c>. So the "seed" is a stable id derived
/// from a fixed namespace GUID (a version 5 UUID, RFC 9562). Every server instance, and every restart, derives the same value
/// from nothing but this code, so no setup input, no stored state and no coordination between instances is needed.
/// </summary>
public static class GitHubBridgePrincipal
{
    /// <summary>The fixed namespace of Bazaar's derived system principal ids. Never change it: it would change every derived id.</summary>
    public static readonly Guid Namespace = new("6f1d3c52-8a0e-5b47-9c21-b7e4d5a90a13");

    /// <summary>The name the GitHub bridge id is derived from.</summary>
    public const string Name = "bazaar.system.github-bridge";

    /// <summary>The GitHub bridge principal id.</summary>
    public static Guid Id { get; } = Derive(Namespace, Name);

    /// <summary>Version 5 (SHA-1, name-based) UUID of <paramref name="name"/> in <paramref name="ns"/>.</summary>
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "RFC 9562 version 5 UUIDs are defined over SHA-1; this derives an identifier, it protects nothing.")]
    public static Guid Derive(Guid ns, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var nsBytes = ns.ToByteArray(bigEndian: true);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[nsBytes.Length + nameBytes.Length];
        nsBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, nsBytes.Length);
        var hash = SHA1.HashData(input);
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
