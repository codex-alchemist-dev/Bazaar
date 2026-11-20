using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Updates;

namespace Bazaar.Updates.Tests;

public sealed class UpdateFeedClientTests
{
    private static readonly Uri _server = new("https://company.example/");
    private static readonly string _sha = new('a', 64);
    private static readonly string _sig = Convert.ToBase64String(new byte[] { 1, 2, 3 });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient ClientFor(FakeHandler handler) =>
        new(handler) { BaseAddress = _server };

    private static string Json(string version = "0.2.0", string url = "https://company.example/Bazaar.msix", string sha = "", string sig = "") =>
        "{\"version\":\"" + version + "\",\"packageUrl\":\"" + url + "\",\"sha256\":\"" + (sha.Length == 0 ? _sha : sha)
        + "\",\"signature\":\"" + (sig.Length == 0 ? _sig : sig) + "\"}";

    [Fact]
    public async Task Latest_route_is_read_into_a_manifest()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Json());
        var client = new UpdateFeedClient(ClientFor(handler));

        var manifest = await client.GetLatestAsync(Ct);

        Assert.NotNull(manifest);
        Assert.Equal("0.2.0", manifest.Version);
        Assert.Equal("https://company.example/Bazaar.msix", manifest.PackageUrl.ToString());
        Assert.Equal(_sha, manifest.Sha256Hex);
        Assert.Equal(new byte[] { 1, 2, 3 }, manifest.Signature);
        Assert.Equal("https://company.example/updates/latest", handler.LastRequestUri);
    }

    [Fact]
    public async Task Not_found_means_no_release_for_this_client()
    {
        var client = new UpdateFeedClient(ClientFor(new FakeHandler(HttpStatusCode.NotFound, string.Empty)));

        Assert.Null(await client.GetLatestAsync(Ct));
    }

    [Fact]
    public async Task Server_error_throws_and_is_not_mistaken_for_no_update()
    {
        var client = new UpdateFeedClient(ClientFor(new FakeHandler(HttpStatusCode.InternalServerError, "boom")));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLatestAsync(Ct));
    }

    [Theory]
    [InlineData("0.2.0", "not a url", "")]
    [InlineData("", "https://company.example/Bazaar.msix", "")]
    public async Task A_malformed_manifest_is_refused(string version, string url, string sha)
    {
        var body = Json(version, url, sha);
        var client = new UpdateFeedClient(ClientFor(new FakeHandler(HttpStatusCode.OK, body)));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(Ct));
    }

    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("AAAA")]
    public async Task A_bad_sha256_is_refused(string sha)
    {
        var client = new UpdateFeedClient(ClientFor(new FakeHandler(HttpStatusCode.OK, Json(sha: sha + new string('0', 64 - sha.Length)))));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(Ct));
    }

    [Fact]
    public async Task A_signature_that_is_not_base64_is_refused()
    {
        var client = new UpdateFeedClient(ClientFor(new FakeHandler(HttpStatusCode.OK, Json(sig: "***not base64***"))));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync(Ct));
    }

    [Fact]
    public void A_client_without_a_base_address_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new UpdateFeedClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}"))));
    }

    [Fact]
    public async Task Download_returns_the_package_bytes()
    {
        var package = Encoding.UTF8.GetBytes("package bytes");
        var handler = new FakeHandler(HttpStatusCode.OK, package);
        var client = new UpdateFeedClient(ClientFor(handler));
        var manifest = new UpdateManifest("0.2.0", new Uri("https://company.example/Bazaar.msix"), _sha, [1]);

        var bytes = await client.DownloadAsync(manifest, Ct);

        Assert.Equal(package, bytes);
    }

    [Fact]
    public async Task Download_refuses_an_insecure_package_url_before_any_request()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "x");
        var client = new UpdateFeedClient(ClientFor(handler));
        var manifest = new UpdateManifest("0.2.0", new Uri("http://company.example/Bazaar.msix"), _sha, [1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync(manifest, Ct));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Download_over_the_size_limit_is_refused()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, new byte[16], declaredLength: UpdateFeedClient.MaxPackageBytes + 1);
        var client = new UpdateFeedClient(ClientFor(handler));
        var manifest = new UpdateManifest("0.2.0", new Uri("https://company.example/Bazaar.msix"), _sha, [1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(manifest, Ct));
    }

    [Fact]
    public async Task Fetched_manifest_and_package_pass_the_existing_verifier()
    {
        // The feed client only fetches. The existing hash-then-signature check decides, so a valid pair must pass it.
        using var rsa = RSA.Create(2048);
        var package = Encoding.UTF8.GetBytes("release package");
        var hex = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var signature = Convert.ToBase64String(rsa.SignData(package, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var body = Json(sha: hex, sig: signature);

        var handler = new FakeHandler(HttpStatusCode.OK, body, package);
        var client = new UpdateFeedClient(ClientFor(handler));
        var manifest = await client.GetLatestAsync(Ct);
        Assert.NotNull(manifest);
        var bytes = await client.DownloadAsync(manifest, Ct);

        var verifier = new FakeVerifier(rsa);
        Assert.True(UpdateVerifier.Verify(manifest, bytes, verifier));
    }

    private sealed class FakeVerifier(RSA key) : IUpdateSignatureVerifier
    {
        public bool Verify(ReadOnlySpan<byte> package, ReadOnlySpan<byte> signature) =>
            key.VerifyData(package, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    /// <summary>Serves a canned body for the latest route, and a separate body for package downloads.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body;
        private readonly byte[]? _package;
        private readonly long? _declaredLength;

        public FakeHandler(HttpStatusCode status, string body)
            : this(status, Encoding.UTF8.GetBytes(body), null, null)
        {
        }

        public FakeHandler(HttpStatusCode status, byte[] body, long? declaredLength = null)
            : this(status, body, null, declaredLength)
        {
        }

        public FakeHandler(HttpStatusCode status, string body, byte[] package)
            : this(status, Encoding.UTF8.GetBytes(body), package, null)
        {
        }

        private FakeHandler(HttpStatusCode status, byte[] body, byte[]? package, long? declaredLength)
        {
            _status = status;
            _body = body;
            _package = package;
            _declaredLength = declaredLength;
        }

        public string? LastRequestUri { get; private set; }

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastRequestUri = request.RequestUri?.ToString();

            bool isPackage = _package is not null && request.RequestUri?.AbsolutePath.EndsWith(".msix", StringComparison.Ordinal) == true;
            var bytes = isPackage ? _package! : _body;

            var response = new HttpResponseMessage(_status)
            {
                Content = new ByteArrayContent(bytes),
            };
            if (_declaredLength is { } declared)
            {
                response.Content.Headers.ContentLength = declared;
            }

            return Task.FromResult(response);
        }
    }
}
