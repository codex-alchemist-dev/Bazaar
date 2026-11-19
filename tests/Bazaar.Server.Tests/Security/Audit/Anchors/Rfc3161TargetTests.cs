using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Bazaar.Server.Security.Audit.Anchors;

namespace Bazaar.Server.Tests.Security.Audit.Anchors;

/// <summary>RFC 3161 request encoding, response parsing and verification, against a stub time-stamp authority.</summary>
public sealed class Rfc3161TargetTests
{
    private const string _endpoint = "https://tsa.example.invalid/tsr";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] _digest = Enumerable.Repeat((byte)0xAB, 32).ToArray();

    private static Rfc3161TargetOptions Options(string endpoint = _endpoint) => new() { Endpoint = endpoint };

    private static Rfc3161Target Target(StubTsa tsa, byte[]? nonce = null) =>
        new(new HttpClient(tsa), Options(), new FixedTime(AnchorFixtures.Now), () => nonce ?? new byte[] { 0x01, 0x02 });

    [Fact]
    public async Task Request_bytes_match_the_hand_built_timestamp_req_vector()
    {
        // TimeStampReq: version 1; messageImprint { SHA-256, 32 bytes of 0xAB }; nonce 0x0102; certReq TRUE.
        // Content: 02 01 01 (3) + messageImprint 30 31 (51) + 02 02 01 02 (4) + 01 01 FF (3) = 61 = 0x3D.
        var expected = AnchorFixtures.Cat(
            AnchorFixtures.Hex("30 3D 02 01 01 30 31 30 0D 06 09 60 86 48 01 65 03 04 02 01 05 00 04 20"),
            Enumerable.Repeat((byte)0xAB, 32).ToArray(),
            AnchorFixtures.Hex("02 02 01 02 01 01 FF"));

        var tsa = new StubTsa(AnchorFixtures.Response(0, AnchorFixtures.Token(AnchorFixtures.TstInfo(_digest, [0x01, 0x02]))));
        await Target(tsa).AnchorAsync(_digest, Ct);

        Assert.Equal(expected, tsa.RequestBody);
        Assert.Equal("application/timestamp-query", tsa.RequestContentType);
        Assert.Equal("https://tsa.example.invalid/tsr", tsa.RequestUri?.ToString());
    }

    [Fact]
    public async Task A_nonce_with_high_bit_set_is_padded_to_a_positive_integer()
    {
        // Nonce 0x80 0x01 needs a leading 0x00 so the INTEGER stays positive: 02 03 00 80 01. Content is 3 + 51 + 5 + 3 = 62 = 0x3E.
        var expected = AnchorFixtures.Cat(
            AnchorFixtures.Hex("30 3E 02 01 01 30 31 30 0D 06 09 60 86 48 01 65 03 04 02 01 05 00 04 20"),
            Enumerable.Repeat((byte)0xAB, 32).ToArray(),
            AnchorFixtures.Hex("02 03 00 80 01 01 01 FF"));

        var tsa = new StubTsa(AnchorFixtures.Response(0, AnchorFixtures.Token(AnchorFixtures.TstInfo(_digest, [0x00, 0x80, 0x01]))));
        await Target(tsa, nonce: [0x80, 0x01]).AnchorAsync(_digest, Ct);

        Assert.Equal(expected, tsa.RequestBody);
    }

    [Fact]
    public async Task A_granted_token_becomes_the_receipt_stamped_at_the_clock_time()
    {
        var token = AnchorFixtures.Token(AnchorFixtures.TstInfo(_digest, [0x01, 0x02]));
        var tsa = new StubTsa(AnchorFixtures.Response(0, token));

        var receipt = await Target(tsa).AnchorAsync(_digest, Ct);

        Assert.Equal("rfc3161", receipt.TargetId);
        Assert.Equal(token, receipt.ReceiptBytes);
        Assert.Equal(AnchorFixtures.Now, receipt.AnchoredAt);
    }

    [Fact]
    public async Task Granted_with_modifications_is_accepted()
    {
        var token = AnchorFixtures.Token(AnchorFixtures.TstInfo(_digest, [0x01, 0x02]));
        var receipt = await Target(new StubTsa(AnchorFixtures.Response(1, token))).AnchorAsync(_digest, Ct);

        Assert.Equal(token, receipt.ReceiptBytes);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task A_rejection_status_throws(int status)
    {
        var tsa = new StubTsa(AnchorFixtures.Response(status, token: null));

        await Assert.ThrowsAsync<InvalidDataException>(() => Target(tsa).AnchorAsync(_digest, Ct));
    }

    [Fact]
    public async Task A_token_over_a_different_digest_is_refused()
    {
        var other = Enumerable.Repeat((byte)0xCD, 32).ToArray();
        var tsa = new StubTsa(AnchorFixtures.Response(0, AnchorFixtures.Token(AnchorFixtures.TstInfo(other, [0x01, 0x02]))));

        await Assert.ThrowsAsync<InvalidDataException>(() => Target(tsa).AnchorAsync(_digest, Ct));
    }

    [Fact]
    public async Task A_token_with_the_wrong_nonce_is_refused()
    {
        var tsa = new StubTsa(AnchorFixtures.Response(0, AnchorFixtures.Token(AnchorFixtures.TstInfo(_digest, [0x09]))));

        await Assert.ThrowsAsync<InvalidDataException>(() => Target(tsa).AnchorAsync(_digest, Ct));
    }

    [Fact]
    public async Task A_token_without_a_nonce_is_refused()
    {
        var tsa = new StubTsa(AnchorFixtures.Response(0, AnchorFixtures.Token(AnchorFixtures.TstInfo(_digest, nonce: null))));

        await Assert.ThrowsAsync<InvalidDataException>(() => Target(tsa).AnchorAsync(_digest, Ct));
    }

    [Fact]
    public async Task An_http_error_status_throws()
    {
        var tsa = new StubTsa(AnchorFixtures.Response(0, AnchorFixtures.Token(AnchorFixtures.TstInfo(_digest, [0x01, 0x02]))))
        {
            Status = HttpStatusCode.ServiceUnavailable,
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => Target(tsa).AnchorAsync(_digest, Ct));
    }

    [Fact]
    public async Task Malformed_response_bytes_are_refused()
    {
        var tsa = new StubTsa([0x30, 0x05, 0x02]);

        await Assert.ThrowsAsync<FormatException>(() => Target(tsa).AnchorAsync(_digest, Ct));
    }

    private static Rfc3161Target VerifyingTarget(params X509Certificate2[] roots) =>
        new(
            new HttpClient(new StubTsa([])),
            new Rfc3161TargetOptions { Endpoint = _endpoint, TrustedRoots = roots },
            new FixedTime(AnchorFixtures.Now));

    private static AuditAnchorReceipt Receipt(byte[] token) => new("rfc3161", token, AnchorFixtures.Now);

    [Fact]
    public async Task Verify_accepts_a_signed_token_that_chains_to_a_pinned_root()
    {
        using var pki = TestPki.Create();
        var token = pki.Sign(AnchorFixtures.TstInfo(_digest, [0x01, 0x02]));
        var target = new Rfc3161Target(
            new HttpClient(new StubTsa(AnchorFixtures.Response(0, token))),
            new Rfc3161TargetOptions { Endpoint = _endpoint, TrustedRoots = [pki.PublicRoot] },
            new FixedTime(AnchorFixtures.Now),
            () => [0x01, 0x02]);

        var receipt = await target.AnchorAsync(_digest, Ct);

        Assert.True(await target.VerifyAsync(receipt, _digest, Ct));
        Assert.False(await target.VerifyAsync(receipt, Enumerable.Repeat((byte)0xCD, 32).ToArray(), Ct));
    }

    [Fact]
    public async Task Verify_rejects_a_flipped_signature_byte()
    {
        using var pki = TestPki.Create();
        var token = pki.Sign(AnchorFixtures.TstInfo(_digest, null));
        token[^1] ^= 0x01;

        Assert.False(await VerifyingTarget(pki.PublicRoot).VerifyAsync(Receipt(token), _digest, Ct));
    }

    [Fact]
    public async Task Verify_accepts_the_unmodified_token_used_by_the_negative_tests()
    {
        using var pki = TestPki.Create();
        var token = pki.Sign(AnchorFixtures.TstInfo(_digest, null));

        Assert.True(await VerifyingTarget(pki.PublicRoot).VerifyAsync(Receipt(token), _digest, Ct));
    }

    [Fact]
    public async Task Verify_rejects_a_token_signed_under_a_root_that_is_not_pinned()
    {
        using var pki = TestPki.Create();
        using var other = TestPki.Create();
        var token = pki.Sign(AnchorFixtures.TstInfo(_digest, null));

        Assert.False(await VerifyingTarget(other.PublicRoot).VerifyAsync(Receipt(token), _digest, Ct));
    }

    [Fact]
    public async Task Verify_rejects_everything_when_no_roots_are_pinned()
    {
        using var pki = TestPki.Create();
        var token = pki.Sign(AnchorFixtures.TstInfo(_digest, null));

        Assert.False(await VerifyingTarget().VerifyAsync(Receipt(token), _digest, Ct));
    }

    [Fact]
    public async Task Verify_rejects_a_token_over_a_different_digest()
    {
        using var pki = TestPki.Create();
        var other = Enumerable.Repeat((byte)0xCD, 32).ToArray();
        var token = pki.Sign(AnchorFixtures.TstInfo(other, null));

        Assert.False(await VerifyingTarget(pki.PublicRoot).VerifyAsync(Receipt(token), _digest, Ct));
    }

    [Fact]
    public async Task Verify_rejects_an_ess_hash_that_names_a_different_certificate()
    {
        using var pki = TestPki.Create();
        using var decoy = TestPki.Create();
        var token = pki.Sign(AnchorFixtures.TstInfo(_digest, null), essCertificate: decoy.Tsa);

        Assert.False(await VerifyingTarget(pki.PublicRoot).VerifyAsync(Receipt(token), _digest, Ct));
    }

    [Fact]
    public async Task Verify_rejects_a_token_whose_signing_certificate_has_no_ess_attribute()
    {
        using var pki = TestPki.Create();
        var token = pki.Sign(AnchorFixtures.TstInfo(_digest, null), includeEss: false);

        Assert.False(await VerifyingTarget(pki.PublicRoot).VerifyAsync(Receipt(token), _digest, Ct));
    }

    [Fact]
    public async Task Verify_rejects_a_gentime_outside_the_tsa_certificate_validity()
    {
        // The TSA certificate was valid for 2025 only; genTime in the token is 2026-11-15.
        using var pki = TestPki.Create(
            tsaNotBefore: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            tsaNotAfter: new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var token = pki.Sign(AnchorFixtures.TstInfo(_digest, null));

        Assert.False(await VerifyingTarget(pki.PublicRoot).VerifyAsync(Receipt(token), _digest, Ct));
    }

    [Fact]
    public async Task Verify_rejects_an_unsigned_placeholder_token()
    {
        using var pki = TestPki.Create();
        var token = AnchorFixtures.Token(AnchorFixtures.TstInfo(_digest, null));

        Assert.False(await VerifyingTarget(pki.PublicRoot).VerifyAsync(Receipt(token), _digest, Ct));
    }

    [Fact]
    public async Task Verify_rejects_garbage_and_foreign_receipts_without_throwing()
    {
        var target = Target(new StubTsa([]));

        Assert.False(await target.VerifyAsync(new AuditAnchorReceipt("rfc3161", [0x30, 0x01], AnchorFixtures.Now), _digest, Ct));
        Assert.False(await target.VerifyAsync(new AuditAnchorReceipt("offsite-git", [0x30], AnchorFixtures.Now), _digest, Ct));
    }

    [Fact]
    public void A_target_with_no_configuration_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new Rfc3161Target(new HttpClient(), new Rfc3161TargetOptions(), new FixedTime(AnchorFixtures.Now)));
    }

    [Theory]
    [InlineData("tsa.example.invalid/tsr")]
    [InlineData("ftp://tsa.example.invalid/tsr")]
    [InlineData("https://user:secret@tsa.example.invalid/tsr")]
    public void An_endpoint_that_is_not_a_plain_http_url_is_refused(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new Rfc3161Target(new HttpClient(), Options(endpoint), new FixedTime(AnchorFixtures.Now)));
    }

    /// <summary>A throwaway root CA and TSA certificate, generated in code, and a signer that builds real TimeStampTokens.</summary>
    private sealed class TestPki : IDisposable
    {
        private const string _tstInfoOid = "1.2.840.113549.1.9.16.1.4";
        private const string _essV2Oid = "1.2.840.113549.1.9.16.2.47";

        private readonly X509Certificate2 _root;

        private TestPki(X509Certificate2 root, X509Certificate2 tsa)
        {
            _root = root;
            Tsa = tsa;
            PublicRoot = X509CertificateLoader.LoadCertificate(root.RawData);
        }

        public X509Certificate2 Tsa { get; }

        public X509Certificate2 PublicRoot { get; }

        public static TestPki Create(DateTimeOffset? tsaNotBefore = null, DateTimeOffset? tsaNotAfter = null)
        {
            var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var to = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest($"CN=Test Root {Guid.NewGuid():N}", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var rootSelfSigned = rootRequest.CreateSelfSigned(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var root = X509CertificateLoader.LoadPkcs12(rootSelfSigned.Export(X509ContentType.Pfx), null);

            using var tsaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var tsaRequest = new CertificateRequest($"CN=Test TSA {Guid.NewGuid():N}", tsaKey, HashAlgorithmName.SHA256);
            tsaRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            tsaRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            tsaRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], true));
            using var tsaPublic = tsaRequest.Create(root, tsaNotBefore ?? from, tsaNotAfter ?? to, RandomNumberGenerator.GetBytes(8));
            using var tsaWithKey = tsaPublic.CopyWithPrivateKey(tsaKey);
            var tsa = X509CertificateLoader.LoadPkcs12(tsaWithKey.Export(X509ContentType.Pfx), null);

            return new TestPki(root, tsa);
        }

        /// <summary>
        /// A TimeStampToken: CMS SignedData over the TSTInfo, signed by the TSA certificate, with the signingCertificateV2
        /// attribute naming <paramref name="essCertificate"/> (default the signer) by SHA-256 hash.
        /// </summary>
        public byte[] Sign(byte[] tstInfo, X509Certificate2? essCertificate = null, bool includeEss = true)
        {
            var cms = new SignedCms(new ContentInfo(new Oid(_tstInfoOid), tstInfo));
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, Tsa)
            {
                DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
                IncludeOption = X509IncludeOption.EndCertOnly,
            };
            if (includeEss)
            {
                var certHash = SHA256.HashData((essCertificate ?? Tsa).RawData);
                var essCertId = AnchorFixtures.Seq(AnchorFixtures.Tlv(0x04, certHash));
                var attribute = AnchorFixtures.Seq(AnchorFixtures.Seq(essCertId));
                signer.SignedAttributes.Add(new AsnEncodedData(new Oid(_essV2Oid), attribute));
            }

            cms.ComputeSignature(signer);
            return cms.Encode();
        }

        public void Dispose()
        {
            _root.Dispose();
            Tsa.Dispose();
            PublicRoot.Dispose();
        }
    }

    /// <summary>A TSA stub: records the request and answers with fixed bytes.</summary>
    private sealed class StubTsa(byte[] reply) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        public byte[]? RequestBody { get; private set; }

        public string? RequestContentType { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestContentType = request.Content?.Headers.ContentType?.MediaType;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent(reply) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-reply");
            return response;
        }
    }

    /// <summary>A clock fixed at one instant.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
