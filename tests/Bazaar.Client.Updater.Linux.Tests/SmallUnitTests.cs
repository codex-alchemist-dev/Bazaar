using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Releases;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Updater.Linux.Tests;

public class SemVerPrecedenceTests
{
    [Theory]
    [InlineData("0.1.0", "0.1.0", 0)]
    [InlineData("0.1.0", "0.1.1", -1)]
    [InlineData("1.0.0", "0.99.99", 1)]
    [InlineData("0.2.0-alpha.1", "0.2.0", -1)]
    [InlineData("0.2.0-alpha.1", "0.2.0-alpha.2", -1)]
    [InlineData("0.2.0-alpha.2", "0.2.0-alpha.10", -1)]
    [InlineData("0.2.0-alpha", "0.2.0-alpha.1", -1)]
    [InlineData("0.2.0-1", "0.2.0-alpha", -1)]
    [InlineData("0.2.0-beta.1", "0.2.0-alpha.9", 1)]
    [InlineData("0.2.0+a", "0.2.0+b", 0)]
    [InlineData("0.2.0-rc.1+x", "0.2.0-rc.1", 0)]
    [InlineData("0.2.0-dev.99999999999999999999", "0.2.0-dev.100000000000000000000", -1)]
    public void Precedence_follows_the_semver_spec(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(SemVerPrecedence.Compare(left, right)));
        Assert.Equal(-expected, Math.Sign(SemVerPrecedence.Compare(right, left)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-a..b")]
    [InlineData("1.0.x")]
    public void Malformed_versions_do_not_parse(string version) =>
        Assert.False(SemVerPrecedence.TryParse(version, out _, out _));
}

public class GpgStatusParsingTests
{
    [Fact]
    public void Only_the_fingerprint_after_a_pub_record_counts_as_a_primary_key()
    {
        string colons = "tru::1\npub:-:255:22:AAAA:1:::-:::scESC:::::ed25519:::0:\nfpr:::::::::AABBCC:\nuid:-::::::::Bazaar:::::::::0:\nsub:-:255:18:BBBB:1:::::e:::::cv25519::\nfpr:::::::::DDEEFF:\n";

        Assert.Equal(["aabbcc"], GpgSignatureVerifier.PrimaryFingerprints(colons));
    }
}

public class HttpReleaseSourceTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = Respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static readonly Uri _manifestUri = new("https://company.example:8443/updates/latest");

    private static ReleaseManifest Sample() =>
        new(
            ReleaseChannels.Stable, "0.2.0", "1.0", "1.0", "1", new string('a', 40),
            [new ReleaseArtifact("linux", "linux-x64", "tar.gz", "Bazaar-0.2.0.tar.gz", 10, new string('0', 64), "Bazaar-0.2.0.tar.gz.sha256", "Bazaar-0.2.0.tar.gz.asc")]);

    [Fact]
    public async Task The_manifest_is_decoded_from_json()
    {
        var handler = new Handler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(Sample(), WireJsonContext.Default.ReleaseManifest)) } };
        var source = new HttpReleaseSource(new HttpClient(handler), _manifestUri);

        var manifest = await source.GetManifestAsync(CancellationToken.None);

        Assert.Equal(Sample(), manifest);
        Assert.Equal(_manifestUri, handler.Requests.Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task No_manifest_is_null(HttpStatusCode code)
    {
        var handler = new Handler { Respond = _ => new HttpResponseMessage(code) };

        Assert.Null(await new HttpReleaseSource(new HttpClient(handler), _manifestUri).GetManifestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_server_error_is_an_exception_the_updater_treats_as_no_manifest()
    {
        var handler = new Handler { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var source = new HttpReleaseSource(new HttpClient(handler), _manifestUri);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetManifestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_manifest_that_breaks_the_contract_rules_is_an_io_error()
    {
        string json = JsonSerializer.Serialize(Sample(), WireJsonContext.Default.ReleaseManifest).Replace("\"0.2.0\"", "\"not-a-version\"", StringComparison.Ordinal);
        var handler = new Handler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) } };
        var source = new HttpReleaseSource(new HttpClient(handler), _manifestUri);

        await Assert.ThrowsAsync<IOException>(() => source.GetManifestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_oversized_manifest_is_refused()
    {
        var handler = new Handler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[HttpReleaseSource.MaxManifestBytes + 1]) } };
        var source = new HttpReleaseSource(new HttpClient(handler), _manifestUri);

        await Assert.ThrowsAsync<IOException>(() => source.GetManifestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_relative_url_resolves_against_the_manifest_address()
    {
        var handler = new Handler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("abc") } };
        var source = new HttpReleaseSource(new HttpClient(handler), _manifestUri);

        await using var stream = await source.OpenAsync("Bazaar-0.2.0.tar.gz", CancellationToken.None);

        Assert.Equal(new Uri("https://company.example:8443/updates/Bazaar-0.2.0.tar.gz"), handler.Requests.Single());
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("abc", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://evil.example/Bazaar.tar.gz")]
    [InlineData("https://company.example/Bazaar.tar.gz")] // Same host, other port.
    [InlineData("http://company.example:8443/Bazaar.tar.gz")] // Same host and port, other scheme.
    public async Task An_absolute_url_on_another_origin_is_never_fetched(string url)
    {
        var handler = new Handler();
        var source = new HttpReleaseSource(new HttpClient(handler), _manifestUri);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.OpenAsync(url, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_absolute_url_on_the_same_origin_is_fetched()
    {
        var handler = new Handler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") } };
        var source = new HttpReleaseSource(new HttpClient(handler), _manifestUri);

        await using var stream = await source.OpenAsync("https://company.example:8443/downloads/a.tar.gz", CancellationToken.None);

        Assert.Equal("/downloads/a.tar.gz", handler.Requests.Single().AbsolutePath);
    }

    [Fact]
    public async Task A_redirect_to_another_origin_is_refused()
    {
        var handler = new Handler
        {
            Respond = r => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("x"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.example/x"),
            },
        };
        var source = new HttpReleaseSource(new HttpClient(handler), _manifestUri);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.OpenAsync("a.tar.gz", CancellationToken.None));
    }

    [Fact]
    public void A_relative_manifest_address_is_refused() =>
        Assert.Throws<ArgumentException>(() => new HttpReleaseSource(new HttpClient(), new Uri("updates/latest", UriKind.Relative)));
}
