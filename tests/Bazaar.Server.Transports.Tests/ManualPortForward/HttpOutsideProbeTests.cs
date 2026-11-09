using System.Net;

namespace Bazaar.Server.Transports.Tests.ManualPortForward;

public sealed class HttpOutsideProbeTests
{
    private static readonly Uri _probeUri = new("https://probe.example.test/check");
    private const string _keyHash = "sha256:abcdef0123456789";

    [Fact]
    public async Task Body_without_the_key_hash_is_not_verified()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>hello</html>") }));
        var probe = new HttpOutsideProbe(client);

        var verified = await probe.ProbeAsync(_probeUri, _keyHash, CancellationToken.None);

        Assert.False(verified);
    }

    [Fact]
    public async Task Body_containing_the_key_hash_is_verified()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"server={_keyHash}") }));
        var probe = new HttpOutsideProbe(client);

        var verified = await probe.ProbeAsync(_probeUri, _keyHash, CancellationToken.None);

        Assert.True(verified);
    }

    [Fact]
    public async Task Error_status_is_not_verified_even_when_the_body_echoes_the_hash()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(_keyHash) }));
        var probe = new HttpOutsideProbe(client);

        var verified = await probe.ProbeAsync(_probeUri, _keyHash, CancellationToken.None);

        Assert.False(verified);
    }
}
