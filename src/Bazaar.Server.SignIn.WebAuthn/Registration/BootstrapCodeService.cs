using System.Security.Cryptography;

namespace Bazaar.Server.SignIn.WebAuthn.Registration;

/// <summary>
/// The one-time operator bootstrap code (doc 32 section 3, doc 45 section 1): 160 random bits shown once to the operator, kept only
/// as a SHA-256 hash in memory, bound to one principal. Five wrong attempts burn it. It is single use: <see cref="TryConsume"/>
/// removes it, and a restart issues a fresh one while the principal still has no passkey.
/// </summary>
public sealed class BootstrapCodeService
{
    public const int MaxFailures = 5;

    private readonly Lock _gate = new();
    private byte[]? _hash;
    private Guid _principalId;
    private int _failures;

    /// <summary>Whether a code is outstanding for <paramref name="principalId"/>.</summary>
    public bool IsActive(Guid principalId)
    {
        lock (_gate)
        {
            return _hash is not null && _principalId == principalId;
        }
    }

    /// <summary>Issues a new code for the principal, replacing any earlier one. The plain code is returned once.</summary>
    public string Issue(Guid principalId)
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        var code = string.Join('-', Enumerable.Range(0, 5).Select(i => raw.Substring(i * 8, 8)));
        lock (_gate)
        {
            _hash = Hash(code);
            _principalId = principalId;
            _failures = 0;
        }

        return code;
    }

    /// <summary>Checks a code without using it. A wrong code counts toward the failure limit.</summary>
    public bool Verify(string? code, Guid principalId)
    {
        var candidate = Hash(code ?? string.Empty);
        lock (_gate)
        {
            if (_hash is null || _principalId != principalId)
            {
                return false;
            }

            if (CryptographicOperations.FixedTimeEquals(_hash, candidate))
            {
                return true;
            }

            if (++_failures >= MaxFailures)
            {
                _hash = null;
            }

            return false;
        }
    }

    /// <summary>Uses the code up. False when it is gone (already used, burned or replaced).</summary>
    public bool TryConsume(Guid principalId)
    {
        lock (_gate)
        {
            if (_hash is null || _principalId != principalId)
            {
                return false;
            }

            _hash = null;
            return true;
        }
    }

    private static byte[] Hash(string code)
    {
        var normalized = new string([.. code.Where(c => c is not ('-' or ' '))]).ToUpperInvariant();
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
    }
}
