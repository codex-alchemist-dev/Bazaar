using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;

namespace Bazaar.Roles.Abstractions;

/// <summary>
/// The owner's signed approval of one grant widening (doc 48 section 4, doc 32 section 5a). The payload is the shared owner
/// payload with action <see cref="WideningArgs.Action"/> and the args hash of this exact change, so one signature cannot be
/// replayed for a different role, person or grant set.
/// </summary>
public sealed record OwnerWideningApproval(OwnerPayload Payload, byte[] Signature);

/// <summary>What the owner's device signs for a widening: the action name and a hash that binds the whole change.</summary>
public static class WideningArgs
{
    /// <summary>The action name the owner payload carries for a grant widening.</summary>
    public const string Action = "roles.widen";

    /// <summary>
    /// SHA-256 over the actor, the role, the person (or none), and the added grants in a fixed order. Each field is
    /// length-prefixed, so no two changes share one hash.
    /// </summary>
    public static byte[] Hash(GrantWideningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sb = new StringBuilder();
        Append(sb, request.Actor.PrincipalId.ToString("D"));
        Append(sb, request.RoleId.ToString("D"));
        Append(sb, request.PrincipalId?.ToString("D") ?? "-");
        foreach (var g in request.Added.Select(Describe).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            Append(sb, g);
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string Describe(Grant g) =>
        string.Create(CultureInfo.InvariantCulture, $"{g.Capability.Value}@{g.Scope.Kind}:{g.Scope.Id}");

    private static void Append(StringBuilder sb, string value) => sb.Append(value.Length).Append(':').Append(value).Append(';');
}

/// <summary>
/// The device biometric check that doc 48 section 8 layers on top of the owner signature. The biometric module registers one.
/// While none is registered the signature alone decides (the same layering as the legal-hold decision, owner card 2026-11-16).
/// Every registered check must pass.
/// </summary>
public interface IWideningBiometricCheck
{
    /// <summary>True when a fresh biometric assertion covers this exact change.</summary>
    Task<bool> PassedAsync(GrantWideningRequest request, CancellationToken ct = default);
}
