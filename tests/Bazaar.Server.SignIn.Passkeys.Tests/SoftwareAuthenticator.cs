using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.SignIn.Passkeys.Tests;

/// <summary>A software ES256 authenticator that builds the same JSON the sign-in page sends.</summary>
internal sealed class SoftwareAuthenticator
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    public byte[] CosePublicKey
    {
        get
        {
            var p = _key.ExportParameters(false);
            var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
            w.WriteStartMap(5);
            w.WriteInt32(1); w.WriteInt32(2);
            w.WriteInt32(3); w.WriteInt32(-7);
            w.WriteInt32(-1); w.WriteInt32(1);
            w.WriteInt32(-2); w.WriteByteString(p.Q.X!);
            w.WriteInt32(-3); w.WriteByteString(p.Q.Y!);
            w.WriteEndMap();
            return w.Encode();
        }
    }

    public string Assert(string rpId, string origin, string challenge, uint counter, byte flags = 0x05, string type = "webauthn.get", bool tamper = false)
    {
        var authData = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpId)).CopyTo(authData, 0);
        authData[32] = flags;
        authData[33] = (byte)(counter >> 24);
        authData[34] = (byte)(counter >> 16);
        authData[35] = (byte)(counter >> 8);
        authData[36] = (byte)counter;

        var clientData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false }));
        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (tamper)
        {
            signature[^1] ^= 0xFF;
        }

        return JsonSerializer.Serialize(new
        {
            id = B64(CredentialId),
            rawId = B64(CredentialId),
            type = "public-key",
            response = new
            {
                clientDataJSON = B64(clientData),
                authenticatorData = B64(authData),
                signature = B64(signature),
                userHandle = (string?)null,
            },
        });
    }

    public static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
