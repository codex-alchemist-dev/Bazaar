using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Approvals;

namespace Bazaar.Server.Biometric.Tests;

/// <summary>A software ES256 authenticator that produces assertions in the doc 48 section 8.1 format (spike S-BIO, B1).</summary>
internal sealed class SoftwareAuthenticator : IDisposable
{
    public const string RelyingParty = "bazaar.test";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public SoftwareAuthenticator(Guid human, string deviceId)
    {
        Human = human;
        DeviceId = deviceId;
        CredentialId = RandomNumberGenerator.GetBytes(32);
    }

    public Guid Human { get; }

    public string DeviceId { get; }

    public byte[] CredentialId { get; }

    public byte[] PublicKeySpki => _key.ExportSubjectPublicKeyInfo();

    public OwnerPayload Payload(string action = "roles.widen", DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        return new OwnerPayload(action, Guid.NewGuid(), SHA256.HashData([1, 2, 3]), null, Human, DeviceId, t, t.AddMinutes(5), RandomNumberGenerator.GetBytes(16));
    }

    public BiometricAssertion Assert(
        OwnerPayload payload,
        uint signCount = 1,
        byte flags = 0x05,
        string factor = "face-depth",
        string liveness = "passed",
        DateTimeOffset? at = null,
        string? rp = null,
        string? origin = null,
        string type = "webauthn.get",
        byte[]? challenge = null,
        int version = 1)
    {
        var auth = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rp ?? RelyingParty), auth);
        auth[32] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(auth.AsSpan(33), signCount);

        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["type"] = type,
            ["challenge"] = Base64Url.EncodeToString(challenge ?? BiometricChallenge.For(payload)),
            ["origin"] = origin ?? "bazaar-device:" + DeviceId,
            ["crossOrigin"] = false,
            ["bazaar"] = new Dictionary<string, object>
            {
                ["factor"] = factor,
                ["liveness"] = liveness,
                ["at"] = (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            },
        });

        var signed = new byte[auth.Length + 32];
        auth.CopyTo(signed, 0);
        SHA256.HashData(json, signed.AsSpan(auth.Length));
        var sig = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new BiometricAssertion(version, CredentialId, auth, json, sig);
    }

    public void Dispose() => _key.Dispose();
}
