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

internal static class SoftwareAttestation
{
    /// <summary>Builds a "none" attestation response for the creation options JSON the server issued, signed by <paramref name="device"/>.</summary>
    public static string Create(SoftwareAuthenticator device, string optionsJson, string rpId, string origin, byte flags = 0x45, string type = "webauthn.create")
    {
        using var doc = JsonDocument.Parse(optionsJson);
        var challenge = doc.RootElement.GetProperty("challenge").GetString()!;

        var cose = device.CosePublicKey;
        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authData.Add(flags);
        authData.AddRange(new byte[] { 0, 0, 0, 0 });
        authData.AddRange(new byte[16]);
        authData.Add((byte)(device.CredentialId.Length >> 8));
        authData.Add((byte)device.CredentialId.Length);
        authData.AddRange(device.CredentialId);
        authData.AddRange(cose);

        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(3);
        w.WriteTextString("fmt"); w.WriteTextString("none");
        w.WriteTextString("attStmt"); w.WriteStartMap(0); w.WriteEndMap();
        w.WriteTextString("authData"); w.WriteByteString(authData.ToArray());
        w.WriteEndMap();

        var clientData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false }));
        return JsonSerializer.Serialize(new
        {
            id = SoftwareAuthenticator.B64(device.CredentialId),
            rawId = SoftwareAuthenticator.B64(device.CredentialId),
            type = "public-key",
            response = new
            {
                attestationObject = SoftwareAuthenticator.B64(w.Encode()),
                clientDataJSON = SoftwareAuthenticator.B64(clientData),
            },
        });
    }
}
