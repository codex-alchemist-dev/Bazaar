using System.Security.Cryptography;

namespace Bazaar.Server.SignIn.WebAuthn.Registration;

/// <summary>A registration challenge bound to the principal and label it was issued for, and to how the caller was authorised.</summary>
public sealed record RegistrationChallenge(string Id, string Challenge, Guid PrincipalId, string Label, bool ViaBootstrap, DateTimeOffset ExpiresAt);

/// <summary>Single-use, short-lived, bounded store of registration challenges. A challenge cannot be used for another principal.</summary>
public sealed class RegistrationChallengeStore(int capacity = 256)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, RegistrationChallenge> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public RegistrationChallenge Issue(Guid principalId, string label, bool viaBootstrap, DateTimeOffset now)
    {
        var entry = new RegistrationChallenge(
            WebAuthnWire.Encode(RandomNumberGenerator.GetBytes(16)),
            WebAuthnWire.Encode(RandomNumberGenerator.GetBytes(32)),
            principalId,
            label,
            viaBootstrap,
            now + Lifetime);
        lock (_gate)
        {
            if (_entries.Count >= capacity)
            {
                foreach (var key in _entries.Where(kv => now >= kv.Value.ExpiresAt).Select(kv => kv.Key).ToArray())
                {
                    _entries.Remove(key);
                }

                while (_entries.Count >= capacity)
                {
                    _entries.Remove(_entries.MinBy(kv => kv.Value.ExpiresAt).Key);
                }
            }

            _entries[entry.Id] = entry;
        }

        return entry;
    }

    /// <summary>Returns the challenge and removes it, or null when unknown, used or expired.</summary>
    public RegistrationChallenge? Consume(string? id, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.Remove(id, out var entry) && now < entry.ExpiresAt ? entry : null;
        }
    }
}
