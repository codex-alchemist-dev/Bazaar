using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.SignIn.WebAuthn.Tests;

/// <summary>A software WebAuthn authenticator that builds registration and assertion responses, so the verifier is tested with real signatures.</summary>
internal sealed class TestAuthenticator
{
    public const byte UserPresent = 0x01;
    public const byte UserVerified = 0x04;
    public const byte BackupEligible = 0x08;
    public const byte BackupState = 0x10;
    public const byte Attested = 0x40;

    public const string RpId = "bazaar.example";
    public const string Origin = "https://bazaar.example";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    public byte[] PublicKeySpki => _key.ExportSubjectPublicKeyInfo();

    public static string B64(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    public static string NewChallenge() => B64(RandomNumberGenerator.GetBytes(32));

    public string Attest(
        string challenge,
        string origin = Origin,
        string rpId = RpId,
        byte flags = UserPresent | UserVerified | Attested,
        string fmt = "none",
        bool emptyStatement = true,
        byte[]? credentialIdInAuthData = null,
        string type = "webauthn.create",
        byte[]? coseOverride = null)
    {
        var credId = credentialIdInAuthData ?? CredentialId;
        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        authData.Add(flags);
        authData.AddRange(new byte[4]);
        authData.AddRange(new byte[16]);
        authData.AddRange([(byte)(credId.Length >> 8), (byte)credId.Length]);
        authData.AddRange(credId);
        authData.AddRange(coseOverride ?? CoseKey());

        var attObj = new List<byte> { 0xA3 };
        attObj.AddRange(Text("fmt"));
        attObj.AddRange(Text(fmt));
        attObj.AddRange(Text("attStmt"));
        attObj.AddRange(emptyStatement ? [0xA0] : [0xA1, .. Text("x"), .. Text("y")]);
        attObj.AddRange(Text("authData"));
        attObj.AddRange(Bytes([.. authData]));

        var clientData = ClientData(type, challenge, origin);
        return JsonSerializer.Serialize(new
        {
            id = B64(CredentialId),
            rawId = B64(CredentialId),
            type = "public-key",
            response = new { clientDataJSON = B64(clientData), attestationObject = B64([.. attObj]) },
        });
    }

    public string Assert(
        string challenge,
        uint counter,
        string origin = Origin,
        string rpId = RpId,
        byte flags = UserPresent | UserVerified,
        byte[]? userHandle = null,
        string type = "webauthn.get",
        ECDsa? signWith = null)
    {
        var authData = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpId), authData);
        authData[32] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(authData.AsSpan(33), counter);
        var clientData = ClientData(type, challenge, origin);
        var signed = new byte[authData.Length + 32];
        authData.CopyTo(signed, 0);
        SHA256.HashData(clientData, signed.AsSpan(authData.Length));
        var signature = (signWith ?? _key).SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
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
                userHandle = userHandle is null ? null : B64(userHandle),
            },
        });
    }

    public byte[] CoseKey(int algorithm = -7)
    {
        var p = _key.ExportParameters(false);
        var cose = new List<byte> { 0xA5, 0x01, 0x02, 0x03 };
        cose.AddRange(algorithm == -7 ? [0x26] : [0x39, 0x01, 0x00]);
        cose.AddRange([0x20, 0x01, 0x21, 0x58, 0x20]);
        cose.AddRange(p.Q.X!);
        cose.AddRange([0x22, 0x58, 0x20]);
        cose.AddRange(p.Q.Y!);
        return [.. cose];
    }

    public static ECDsa NewOtherKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static byte[] ClientData(string type, string challenge, string origin) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type, challenge, origin, crossOrigin = false });

    private static byte[] Text(string s) => Head(3, Encoding.UTF8.GetBytes(s));

    private static byte[] Bytes(byte[] b) => Head(2, b);

    private static byte[] Head(int major, byte[] payload)
    {
        var len = payload.Length;
        List<byte> head = len < 24 ? [(byte)((major << 5) | len)]
            : len < 256 ? [(byte)((major << 5) | 24), (byte)len]
            : [(byte)((major << 5) | 25), (byte)(len >> 8), (byte)len];
        head.AddRange(payload);
        return [.. head];
    }
}
