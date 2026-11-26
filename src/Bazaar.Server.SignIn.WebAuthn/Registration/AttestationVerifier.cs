using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.SignIn.WebAuthn.Cbor;

namespace Bazaar.Server.SignIn.WebAuthn.Registration;

/// <summary>A verified registration: the credential id, the public key as SubjectPublicKeyInfo DER, and the first sign count.</summary>
public sealed record VerifiedAttestation(byte[] CredentialId, byte[] PublicKey, long SignCount);

/// <summary>
/// Verifies a WebAuthn registration response (doc 32 section 4). The server asks for attestation "none" and accepts only that
/// format with an empty statement: a passkey here proves possession of a key, not the make of the device. Checked: the client
/// data (type <c>webauthn.create</c>, the issued challenge, an allowed origin), the rp-id hash, user presence and verification,
/// an attested credential whose id matches the raw id, an ES256 P-256 key that is a valid curve point, and no trailing bytes.
/// Anything else returns null with no reason. Hostile input never throws.
/// </summary>
public sealed class AttestationVerifier(WebAuthnOptions options)
{
    private const int _maxCredentialIdBytes = 1023;
    private const int _minCredentialIdBytes = 16;
    private const int _maxAttestationBytes = 8192;
    private const long _coseEs256 = -7;

    private readonly byte[]? _rpIdHash = options.RpId is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(options.RpId));

    public VerifiedAttestation? Verify(string challenge, string attestationJson)
    {
        try
        {
            return VerifyCore(challenge, attestationJson);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    private VerifiedAttestation? VerifyCore(string challenge, string attestationJson)
    {
        if (_rpIdHash is null || !options.IsConfigured || string.IsNullOrEmpty(challenge)
            || string.IsNullOrEmpty(attestationJson) || attestationJson.Length > WebAuthnWire.MaxEnvelopeChars)
        {
            return null;
        }

        var envelope = JsonSerializer.Deserialize(attestationJson, WebAuthnJson.Default.AttestationEnvelope);
        if (envelope?.Response is not { } response || envelope.Type != "public-key")
        {
            return null;
        }

        var rawId = WebAuthnWire.Decode(envelope.RawId, _maxCredentialIdBytes);
        var clientData = WebAuthnWire.Decode(response.ClientDataJSON, WebAuthnWire.MaxClientDataBytes);
        var attestationObject = WebAuthnWire.Decode(response.AttestationObject, _maxAttestationBytes);
        if (rawId is null || rawId.Length < _minCredentialIdBytes || clientData is null || attestationObject is null
            || !WebAuthnWire.ClientDataValid(clientData, "webauthn.create", challenge, options))
        {
            return null;
        }

        var authData = ReadAuthenticatorData(attestationObject);
        if (authData is null || authData.Length < 55 || !CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), _rpIdHash))
        {
            return null;
        }

        var flags = authData[32];
        if ((flags & WebAuthnWire.FlagUserPresent) == 0
            || (flags & WebAuthnWire.FlagUserVerified) == 0
            || (flags & WebAuthnWire.FlagAttested) == 0
            || (flags & WebAuthnWire.FlagExtensions) != 0
            || ((flags & WebAuthnWire.FlagBackupState) != 0 && (flags & WebAuthnWire.FlagBackupEligible) == 0))
        {
            return null;
        }

        var signCount = BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4));

        // After the 37-byte header: aaguid (16), credential id length (2), credential id, then the COSE key, and nothing else.
        var idLength = BinaryPrimitives.ReadUInt16BigEndian(authData.AsSpan(53, 2));
        if (idLength != rawId.Length || authData.Length < 55 + idLength)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(authData.AsSpan(55, idLength), rawId))
        {
            return null;
        }

        var key = ReadEs256Key(authData.AsSpan(55 + idLength));
        return key is null ? null : new VerifiedAttestation(rawId, key, signCount);
    }

    /// <summary>Reads <c>{fmt: "none", attStmt: {}, authData: bytes}</c>; any other shape or format is refused.</summary>
    private static byte[]? ReadAuthenticatorData(byte[] attestationObject)
    {
        var reader = new MiniCbor(attestationObject);
        var entries = reader.ReadMapHeader(3);
        if (entries != 3)
        {
            return null;
        }

        string? fmt = null;
        var statementEmpty = false;
        byte[]? authData = null;
        for (var i = 0; i < entries; i++)
        {
            switch (reader.ReadText(16))
            {
                case "fmt" when fmt is null:
                    fmt = reader.ReadText(16);
                    break;
                case "attStmt" when !statementEmpty:
                    statementEmpty = reader.ReadMapHeader(0) == 0;
                    break;
                case "authData" when authData is null:
                    authData = reader.ReadBytes(_maxAttestationBytes);
                    break;
                default:
                    return null;
            }
        }

        return fmt == "none" && statementEmpty && reader.Remaining == 0 ? authData : null;
    }

    /// <summary>Reads a COSE_Key that must be ES256 on P-256 and the only thing left in <paramref name="cose"/>. Returns SubjectPublicKeyInfo DER.</summary>
    private static byte[]? ReadEs256Key(ReadOnlySpan<byte> cose)
    {
        if (cose.Length is 0 or > 256)
        {
            return null;
        }

        var reader = new MiniCbor(cose);
        var entries = reader.ReadMapHeader(8);
        long? kty = null, alg = null, crv = null;
        byte[]? x = null, y = null;
        for (var i = 0; i < entries; i++)
        {
            var label = reader.ReadInt();
            if (reader.PeekMajor() > 1)
            {
                var bytes = reader.ReadBytes(64);
                switch (label)
                {
                    case -2 when x is null:
                        x = bytes;
                        break;
                    case -3 when y is null:
                        y = bytes;
                        break;
                    default:
                        return null;
                }

                continue;
            }

            var value = reader.ReadInt();
            switch (label)
            {
                case 1 when kty is null:
                    kty = value;
                    break;
                case 3 when alg is null:
                    alg = value;
                    break;
                case -1 when crv is null:
                    crv = value;
                    break;
                default:
                    return null;
            }
        }

        if (reader.Remaining != 0 || kty != 2 || alg != _coseEs256 || crv != 1 || x is not { Length: 32 } || y is not { Length: 32 })
        {
            return null;
        }

        using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
        return ecdsa.ExportSubjectPublicKeyInfo();
    }
}
