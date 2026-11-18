using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Backup.Recovery;

/// <summary>
/// Verifies WebAuthn assertions from backup-owner FIDO2 security keys (doc 48 section 7). Every check is required:
/// rpIdHash, user presence, user verification, a single-device credential (backup-eligible, i.e. synced passkey,
/// credentials are refused), type, challenge, origin, signature, registered credential, and a strictly increasing
/// sign count. Hostile input never throws; it returns a refusal with a fixed reason code. Real authenticator
/// interoperability is not covered by the unit tests.
/// </summary>
/// <remarks>
/// Assertion bytes use the layout produced by <see cref="EncodeAssertion"/>. The challenge is
/// <see cref="ComputeChallenge"/> over the case id and a per-case nonce the caller issued. Sign count: a stored count
/// of zero (new credential, or an authenticator without a counter) accepts the first assertion; a non-zero stored
/// count requires a strictly greater one, otherwise a clone is suspected and the assertion is refused. The caller
/// stores <see cref="Fido2AssertionResult.NewSignCount"/> after acceptance.
/// </remarks>
public sealed class Fido2AssertionVerifier : IHardwareKeyAssertionVerifier
{
    private const int _maxAssertionBytes = 8192;
    private const int _maxClientDataBytes = 2048;
    private const int _minNonceBytes = 16;
    private const byte _flagUserPresent = 0x01;
    private const byte _flagUserVerified = 0x04;
    private const byte _flagBackupEligible = 0x08;
    private const long _coseEs256 = -7;
    private const long _coseRs256 = -257;

    private readonly Fido2Options _options;
    private readonly byte[] _rpIdHash;
    private readonly Func<string, IReadOnlyList<Fido2Credential>> _credentialsForMember;
    private readonly Func<string, byte[]?> _nonceForCase;

    /// <summary>Creates the verifier.</summary>
    /// <param name="options">Relying-party id and allowed origins.</param>
    /// <param name="credentialsForMember">Registered credentials of a backup owner, by member id.</param>
    /// <param name="nonceForCase">The nonce the server issued for a recovery case (at least 16 bytes), or null if none.</param>
    public Fido2AssertionVerifier(
        Fido2Options options,
        Func<string, IReadOnlyList<Fido2Credential>> credentialsForMember,
        Func<string, byte[]?> nonceForCase)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialsForMember);
        ArgumentNullException.ThrowIfNull(nonceForCase);
        _options = options;
        _credentialsForMember = credentialsForMember;
        _nonceForCase = nonceForCase;
        _rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.RelyingPartyId));
    }

    /// <summary>The challenge a client must sign for a case: SHA-256 over a domain tag, the case id and the nonce.</summary>
    public static byte[] ComputeChallenge(string caseId, ReadOnlySpan<byte> nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);
        var id = Encoding.UTF8.GetBytes(caseId);
        var buf = new byte[19 + id.Length + 1 + nonce.Length];
        Encoding.ASCII.GetBytes("bazaar-recovery-v1\0", buf);
        id.CopyTo(buf, 19);
        nonce.CopyTo(buf.AsSpan(19 + id.Length + 1));
        return SHA256.HashData(buf);
    }

    /// <summary>Packs an assertion: u16-length-prefixed credential id, authenticatorData, clientDataJSON and signature.</summary>
    public static byte[] EncodeAssertion(ReadOnlySpan<byte> credentialId, ReadOnlySpan<byte> authenticatorData, ReadOnlySpan<byte> clientDataJson, ReadOnlySpan<byte> signature)
    {
        using var ms = new MemoryStream();
        foreach (var part in new[] { credentialId.ToArray(), authenticatorData.ToArray(), clientDataJson.ToArray(), signature.ToArray() })
        {
            if (part.Length > ushort.MaxValue)
            {
                throw new ArgumentException("A part is too long.");
            }

            var len = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)part.Length);
            ms.Write(len);
            ms.Write(part);
        }

        return ms.ToArray();
    }

    /// <inheritdoc />
    public bool Verify(string caseId, string memberId, ReadOnlyMemory<byte> assertion) =>
        VerifyDetailed(caseId, memberId, assertion).Accepted;

    /// <summary>Verifies and returns the outcome, including the sign count to store on acceptance.</summary>
    public Fido2AssertionResult VerifyDetailed(string caseId, string memberId, ReadOnlyMemory<byte> assertion)
    {
        ArgumentNullException.ThrowIfNull(caseId);
        ArgumentNullException.ThrowIfNull(memberId);

        try
        {
            return VerifyCore(caseId, memberId, assertion.Span);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException or JsonException or InvalidOperationException or OverflowException)
        {
            return Fido2AssertionResult.Refuse("malformed");
        }
    }

    private Fido2AssertionResult VerifyCore(string caseId, string memberId, ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > _maxAssertionBytes || !TryUnpack(data, out var credId, out var authData, out var clientData, out var signature))
        {
            return Fido2AssertionResult.Refuse("malformed");
        }

        if (clientData.Length > _maxClientDataBytes || authData.Length < 37)
        {
            return Fido2AssertionResult.Refuse("malformed");
        }

        var credential = FindCredential(memberId, credId);
        if (credential is null)
        {
            return Fido2AssertionResult.Refuse("unknown-credential");
        }

        if (!CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), _rpIdHash))
        {
            return Fido2AssertionResult.Refuse("rp-id-mismatch");
        }

        var flags = authData[32];
        if ((flags & _flagUserPresent) == 0)
        {
            return Fido2AssertionResult.Refuse("user-presence-missing");
        }

        if ((flags & _flagUserVerified) == 0)
        {
            return Fido2AssertionResult.Refuse("user-verification-missing");
        }

        if ((flags & _flagBackupEligible) != 0)
        {
            return Fido2AssertionResult.Refuse("synced-passkey-not-allowed");
        }

        var clientRefusal = CheckClientData(caseId, clientData);
        if (clientRefusal is not null)
        {
            return Fido2AssertionResult.Refuse(clientRefusal);
        }

        var signed = new byte[authData.Length + 32];
        authData.CopyTo(signed);
        SHA256.HashData(clientData, signed.AsSpan(authData.Length));

        if (!VerifySignature(credential.CoseKey, signed, signature))
        {
            return Fido2AssertionResult.Refuse("bad-signature");
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4));
        if (credential.SignCount != 0 && count <= credential.SignCount)
        {
            return Fido2AssertionResult.Refuse("sign-count-not-increasing");
        }

        return Fido2AssertionResult.Accept(credential.CredentialId, count);
    }

    private string? CheckClientData(string caseId, ReadOnlySpan<byte> clientData)
    {
        using var doc = JsonDocument.Parse(clientData.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "malformed";
        }

        if (!TryString(root, "type", out var type) || type != "webauthn.get")
        {
            return "wrong-type";
        }

        if (!TryString(root, "challenge", out var challenge))
        {
            return "challenge-mismatch";
        }

        var nonce = _nonceForCase(caseId);
        if (nonce is null || nonce.Length < _minNonceBytes)
        {
            return "challenge-mismatch";
        }

        var expected = ComputeChallenge(caseId, nonce);
        var actual = DecodeBase64Url(challenge);
        if (actual is null || !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return "challenge-mismatch";
        }

        if (!TryString(root, "origin", out var origin) || !_options.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
        {
            return "origin-not-allowed";
        }

        if (root.TryGetProperty("crossOrigin", out var cross) && cross.ValueKind != JsonValueKind.False)
        {
            return "cross-origin";
        }

        return null;
    }

    private Fido2Credential? FindCredential(string memberId, ReadOnlySpan<byte> credId)
    {
        foreach (var c in _credentialsForMember(memberId))
        {
            if (c.CredentialId is { Length: > 0 } id && id.Length == credId.Length && CryptographicOperations.FixedTimeEquals(id, credId))
            {
                return c;
            }
        }

        return null;
    }

    private bool VerifySignature(byte[] coseKey, byte[] signed, ReadOnlySpan<byte> signature)
    {
        var map = BoundedCbor.ReadIntKeyedMap(coseKey);
        if (map is null || !map.TryGetValue(3, out var alg) || alg.Int is not { } algId || !map.TryGetValue(1, out var kty) || kty.Int is not { } ktyId)
        {
            return false;
        }

        if (algId == _coseEs256 && ktyId == 2)
        {
            if (!map.TryGetValue(-1, out var crv) || crv.Int != 1
                || !map.TryGetValue(-2, out var x) || x.Bytes is not { Length: 32 } xb
                || !map.TryGetValue(-3, out var y) || y.Bytes is not { Length: 32 } yb)
            {
                return false;
            }

            using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = xb, Y = yb } });
            return ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }

        if (algId == _coseRs256 && ktyId == 3 && _options.AllowRs256)
        {
            if (!map.TryGetValue(-1, out var n) || n.Bytes is not { Length: >= 256 and <= 512 } nb
                || !map.TryGetValue(-2, out var e) || e.Bytes is not { Length: >= 1 and <= 8 } eb)
            {
                return false;
            }

            using var rsa = RSA.Create(new RSAParameters { Modulus = nb, Exponent = eb });
            return rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        return false;
    }

    private static bool TryUnpack(ReadOnlySpan<byte> data, out byte[] credId, out byte[] authData, out byte[] clientData, out byte[] signature)
    {
        credId = authData = clientData = signature = [];
        var pos = 0;
        var parts = new byte[4][];
        for (var i = 0; i < 4; i++)
        {
            if (pos + 2 > data.Length)
            {
                return false;
            }

            int len = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            pos += 2;
            if (len == 0 || len > data.Length - pos)
            {
                return false;
            }

            parts[i] = data.Slice(pos, len).ToArray();
            pos += len;
        }

        if (pos != data.Length)
        {
            return false;
        }

        (credId, authData, clientData, signature) = (parts[0], parts[1], parts[2], parts[3]);
        return true;
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = "";
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String && el.GetString() is { } s)
        {
            value = s;
            return true;
        }

        return false;
    }

    private static byte[]? DecodeBase64Url(string s)
    {
        if (s.Length is 0 or > 256)
        {
            return null;
        }

        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
