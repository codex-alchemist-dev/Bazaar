using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Abstractions.Wire;

namespace Bazaar.Client.Core.Tests.Wire;

public class EndpointWireClientTests
{
    private static readonly string _pin = new('a', 64);

    [Fact]
    public void Loopback_http_becomes_ws_on_the_same_port()
    {
        var uri = EndpointWireClient.SocketUri(new ServerEndpoint(new Uri("http://127.0.0.1:8080/")));

        Assert.Equal("ws://127.0.0.1:8080/ws", uri.ToString());
    }

    [Fact]
    public void Https_becomes_wss_and_keeps_an_explicit_port()
    {
        Assert.Equal("wss://host.example/ws", EndpointWireClient.SocketUri(new ServerEndpoint(new Uri("https://host.example/"), KeyHashPin: _pin)).ToString());
        Assert.Equal("wss://host.example:8443/ws", EndpointWireClient.SocketUri(new ServerEndpoint(new Uri("https://host.example:8443/"), KeyHashPin: _pin)).ToString());
    }

    [Fact]
    public async Task An_https_endpoint_without_a_pin_is_refused_before_any_connection()
    {
        var transport = EndpointWireClient.CreateTransport(new ServerEndpoint(new Uri("https://host.example/")));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Contains("pin", failure.Message);
    }

    [Fact]
    public async Task Plain_http_off_loopback_is_refused()
    {
        var transport = EndpointWireClient.CreateTransport(new ServerEndpoint(new Uri("http://host.example/")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.ConnectAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Create_returns_a_client_that_has_not_connected()
    {
        var client = EndpointWireClient.Create(ServerEndpoint.Development, new InMemoryCursorStore(), "android-1", "android", "test");

        Assert.Equal(0, client.ConnectionCount);
        Assert.IsAssignableFrom<IWireRequester>(client);
    }
}
