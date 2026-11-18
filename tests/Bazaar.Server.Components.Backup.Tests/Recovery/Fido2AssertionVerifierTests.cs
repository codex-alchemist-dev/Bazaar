using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Components.Backup.Recovery;

namespace Bazaar.Server.Components.Backup.Tests.Recovery;

public sealed class Fido2AssertionVerifierTests
{
    private const string _rp = "bazaar.example.invalid";
    private const string _origin = "https://bazaar.example.invalid";
    private const string _caseId = "case-1";
    private static readonly byte[] _nonce = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] _credId = [9, 8, 7, 6, 5, 4, 3, 2];
    private static readonly DateTimeOffset _t0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public Rig(uint storedCount = 0)
        {
            Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Credential = new Fido2Credential(_credId, CoseEs256(Key), storedCount);
            Verifier = Make(Credential);
        }

        public ECDsa Key { get; }

        public Fido2Credential Credential { get; }

        public Fido2AssertionVerifier Verifier { get; }

        public static Fido2AssertionVerifier Make(params Fido2Credential[] creds) =>
            new(new Fido2Options(_rp, [_origin]), _ => creds, _ => _nonce);
    }

    private static byte[] CoseEs256(ECDsa key)
    {
        var p = key.ExportParameters(false);
        return
        [
            0xA5,
            0x01, 0x02,
            0x03, 0x26,
            0x20, 0x01,
            0x21, 0x58, 0x20, .. p.Q.X!,
            0x22, 0x58, 0x20, .. p.Q.Y!,
        ];
    }

    private static byte[] AuthData(string rpId = _rp, byte flags = 0x05, uint count = 1)
    {
        var d = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(rpId)).CopyTo(d, 0);
        d[32] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(33), count);
        return d;
    }

    private static byte[] ClientData(string? challenge = null, string type = "webauthn.get", string origin = _origin)
    {
        challenge ??= Base64Url(Fido2AssertionVerifier.ComputeChallenge(_caseId, _nonce));
        return Encoding.UTF8.GetBytes($"{{\"type\":\"{type}\",\"challenge\":\"{challenge}\",\"origin\":\"{origin}\"}}");
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Sign(ECDsa key, byte[] authData, byte[] clientData)
    {
        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        return key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static byte[] Assertion(Rig rig, byte[]? authData = null, byte[]? clientData = null, byte[]? credId = null, ECDsa? signer = null)
    {
        authData ??= AuthData();
        clientData ??= ClientData();
        var sig = Sign(signer ?? rig.Key, authData, clientData);
        return Fido2AssertionVerifier.EncodeAssertion(credId ?? _credId, authData, clientData, sig);
    }

    private static void AssertRefused(Rig rig, byte[] assertion, string reason)
    {
        var r = rig.Verifier.VerifyDetailed(_caseId, "backup-1", assertion);
        Assert.False(r.Accepted);
        Assert.Equal(reason, r.Reason);
        Assert.False(rig.Verifier.Verify(_caseId, "backup-1", assertion));
    }

    [Fact]
    public void Valid_assertion_is_accepted_and_returns_new_count()
    {
        var rig = new Rig();
        var r = rig.Verifier.VerifyDetailed(_caseId, "backup-1", Assertion(rig));
        Assert.True(r.Accepted);
        Assert.Equal(1u, r.NewSignCount);
        Assert.Equal(_credId, r.CredentialId);
    }

    [Fact]
    public void Wrong_rp_id_is_refused()
    {
        var rig = new Rig();
        AssertRefused(rig, Assertion(rig, authData: AuthData("other.example.invalid")), "rp-id-mismatch");
    }

    [Fact]
    public void User_verification_missing_is_refused()
    {
        var rig = new Rig();
        AssertRefused(rig, Assertion(rig, authData: AuthData(flags: 0x01)), "user-verification-missing");
    }

    [Fact]
    public void User_presence_missing_is_refused()
    {
        var rig = new Rig();
        AssertRefused(rig, Assertion(rig, authData: AuthData(flags: 0x04)), "user-presence-missing");
    }

    [Fact]
    public void Synced_passkey_flag_is_refused()
    {
        var rig = new Rig();
        AssertRefused(rig, Assertion(rig, authData: AuthData(flags: 0x0D)), "synced-passkey-not-allowed");
    }

    [Fact]
    public void Wrong_challenge_is_refused()
    {
        var rig = new Rig();
        var other = Base64Url(Fido2AssertionVerifier.ComputeChallenge("case-2", _nonce));
        AssertRefused(rig, Assertion(rig, clientData: ClientData(other)), "challenge-mismatch");
    }

    [Fact]
    public void Wrong_type_is_refused()
    {
        var rig = new Rig();
        AssertRefused(rig, Assertion(rig, clientData: ClientData(type: "webauthn.create")), "wrong-type");
    }

    [Fact]
    public void Wrong_origin_is_refused()
    {
        var rig = new Rig();
        AssertRefused(rig, Assertion(rig, clientData: ClientData(origin: "https://evil.example.invalid")), "origin-not-allowed");
    }

    [Fact]
    public void Bad_signature_is_refused()
    {
        var rig = new Rig();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        AssertRefused(rig, Assertion(rig, signer: other), "bad-signature");
    }

    [Fact]
    public void Tampered_client_data_after_signing_is_refused()
    {
        var rig = new Rig();
        var good = AuthData();
        var signedOver = ClientData();
        var sig = Sign(rig.Key, good, signedOver);
        var sent = ClientData(origin: _origin + "");
        sent[^2] = (byte)' ';
        var a = Fido2AssertionVerifier.EncodeAssertion(_credId, good, sent, sig);
        Assert.False(rig.Verifier.Verify(_caseId, "backup-1", a));
    }

    [Fact]
    public void Unknown_credential_is_refused()
    {
        var rig = new Rig();
        AssertRefused(rig, Assertion(rig, credId: [1, 2, 3]), "unknown-credential");
    }

    [Fact]
    public void Credential_of_another_member_is_refused()
    {
        var rig = new Rig();
        var v = new Fido2AssertionVerifier(new Fido2Options(_rp, [_origin]), m => m == "backup-2" ? [rig.Credential] : [], _ => _nonce);
        Assert.False(v.Verify(_caseId, "backup-1", Assertion(rig)));
    }

    [Theory]
    [InlineData(5u, 5u)]
    [InlineData(5u, 3u)]
    public void Non_increasing_sign_count_is_refused(uint stored, uint presented)
    {
        var rig = new Rig(stored);
        AssertRefused(rig, Assertion(rig, authData: AuthData(count: presented)), "sign-count-not-increasing");
    }

    [Fact]
    public void Increasing_sign_count_is_accepted()
    {
        var rig = new Rig(5);
        var r = rig.Verifier.VerifyDetailed(_caseId, "backup-1", Assertion(rig, authData: AuthData(count: 6)));
        Assert.True(r.Accepted);
        Assert.Equal(6u, r.NewSignCount);
    }

    [Fact]
    public void Missing_nonce_is_refused()
    {
        var rig = new Rig();
        var v = new Fido2AssertionVerifier(new Fido2Options(_rp, [_origin]), _ => [rig.Credential], _ => null);
        Assert.False(v.Verify(_caseId, "backup-1", Assertion(rig)));
    }

    [Fact]
    public void Malformed_cose_key_is_refused_without_throwing()
    {
        var rig = new Rig();
        byte[][] bad =
        [
            [0xA1, 0x01],
            [0xFF, 0xFF, 0xFF],
            [0xBF, 0x01, 0x02, 0xFF],
            [0xA1, 0x01, 0xA0],
            CoseEs256(rig.Key)[..40],
            [],
        ];
        foreach (var key in bad)
        {
            var v = Rig.Make(new Fido2Credential(_credId, key, 0));
            Assert.False(v.Verify(_caseId, "backup-1", Assertion(rig)));
        }
    }

    [Fact]
    public void Oversized_cbor_is_refused()
    {
        var rig = new Rig();
        var good = CoseEs256(rig.Key);
        Assert.True(Rig.Make(new Fido2Credential(_credId, good, 0)).Verify(_caseId, "backup-1", Assertion(rig)));

        // A structurally valid key with a 2000 byte extra byte string (key 5) exceeds the 1024 byte cap.
        byte[] padded = [0xA6, .. good.AsSpan(1), 0x05, 0x59, 0x07, 0xD0, .. new byte[2000]];
        var v = Rig.Make(new Fido2Credential(_credId, padded, 0));
        Assert.False(v.Verify(_caseId, "backup-1", Assertion(rig)));
    }

    [Fact]
    public void Indefinite_length_and_nested_cbor_is_refused()
    {
        var rig = new Rig();
        var good = CoseEs256(rig.Key);
        byte[] indefinite = [0xA5, .. good.AsSpan(1, 5), 0x21, 0x5F, 0x41, 0x01, 0xFF, 0x22, 0x41, 0x01];
        byte[] nested = [0xA6, .. good.AsSpan(1), 0x05, 0x81, 0x01];
        foreach (var key in new[] { indefinite, nested })
        {
            Assert.False(Rig.Make(new Fido2Credential(_credId, key, 0)).Verify(_caseId, "backup-1", Assertion(rig)));
        }
    }

    [Fact]
    public void Hostile_assertion_bytes_never_throw()
    {
        var rig = new Rig();
        var rng = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var junk = new byte[rng.Next(0, 300)];
            rng.NextBytes(junk);
            Assert.False(rig.Verifier.Verify(_caseId, "backup-1", junk));
        }

        Assert.False(rig.Verifier.Verify(_caseId, "backup-1", new byte[20000]));
        Assert.False(rig.Verifier.Verify(_caseId, "backup-1", ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Malformed_client_data_json_is_refused()
    {
        var rig = new Rig();
        AssertRefused(rig, Assertion(rig, clientData: Encoding.UTF8.GetBytes("{not json")), "malformed");
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var rig = new Rig();
        Assert.Throws<ArgumentNullException>(() => rig.Verifier.Verify(null!, "m", new byte[1]));
        Assert.Throws<ArgumentNullException>(() => new Fido2AssertionVerifier(null!, _ => [], _ => null));
    }

    [Fact]
    public void Options_validate()
    {
        Assert.Throws<ArgumentException>(() => new Fido2Options("", [_origin]));
        Assert.Throws<ArgumentException>(() => new Fido2Options(_rp, []));
        Assert.Throws<ArgumentException>(() => new Fido2Options(_rp, ["http://bazaar.example.invalid"]));
        Assert.Throws<ArgumentException>(() => new Fido2Options(_rp, ["https://bazaar.example.invalid/path"]));
    }

    [Fact]
    public void Accepted_assertion_counts_toward_threshold_and_duplicate_counts_once()
    {
        var rig = new Rig();
        var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credB = new Fido2Credential([1, 1, 1, 1], CoseEs256(other), 0);
        var v = new Fido2AssertionVerifier(
            new Fido2Options(_rp, [_origin]),
            m => m == "backup-1" ? [rig.Credential] : m == "backup-2" ? [credB] : [],
            _ => _nonce);

        var c = RecoveryCase.Initiate(_caseId, QuorumPolicy.Default, "backup-1", ["device-a"], ["backup-1", "backup-2", "backup-3"], _t0).Case
            .StartTimeLock(_t0).Case;

        var a1 = Assertion(rig);
        var authB = AuthData();
        var cdB = ClientData();
        var a2 = Fido2AssertionVerifier.EncodeAssertion([1, 1, 1, 1], authB, cdB, Sign(other, authB, cdB));

        c = c.AcceptAssertion("backup-1", a1, v, _t0.AddHours(1)).Case;
        c = c.AcceptAssertion("backup-1", a1, v, _t0.AddHours(2)).Case;
        Assert.Single(c.AcceptedMembers);

        Assert.False(c.Complete(_t0.AddHours(80)).Succeeded);

        c = c.AcceptAssertion("backup-2", a2, v, _t0.AddHours(3)).Case;
        Assert.Equal(2, c.AcceptedMembers.Count);
        Assert.True(c.Complete(_t0.AddHours(73)).Succeeded);

        var bad = c.AcceptAssertion("backup-3", a1, v, _t0.AddHours(4));
        Assert.False(bad.Succeeded);
        other.Dispose();
    }

    [Fact]
    public void Rs256_is_refused_unless_enabled_and_accepted_when_enabled()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        byte[] cose = [0xA4, 0x01, 0x03, 0x03, 0x39, 0x01, 0x00, 0x20, 0x59, 0x01, 0x00, .. p.Modulus!, 0x21, 0x43, .. p.Exponent!];
        var cred = new Fido2Credential(_credId, cose, 0);
        var auth = AuthData();
        var cd = ClientData();
        var sig = rsa.SignData(auth.Concat(SHA256.HashData(cd)).ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var a = Fido2AssertionVerifier.EncodeAssertion(_credId, auth, cd, sig);

        var off = new Fido2AssertionVerifier(new Fido2Options(_rp, [_origin]), _ => [cred], _ => _nonce);
        var on = new Fido2AssertionVerifier(new Fido2Options(_rp, [_origin], allowRs256: true), _ => [cred], _ => _nonce);
        Assert.False(off.Verify(_caseId, "m", a));
        Assert.True(on.Verify(_caseId, "m", a));
    }
}
