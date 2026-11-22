using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bazaar.ModulePermissions.Http.Tests;

public class ModuleHttpTests
{
    private const string _module = "bazaar.weather";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ListSink : IModulePermissionDenialSink
    {
        public List<ModulePermissionDenial> Denials { get; } = [];

        public void Record(ModulePermissionDenial denial) => Denials.Add(denial);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri?> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (ModulePermissionGate Gate, ListSink Sink) Gate(params string[] permissions)
    {
        var sink = new ListSink();
        var gate = new ModulePermissionGate(sink, TimeProvider.System);
        gate.Activate(_module, permissions);
        return (gate, sink);
    }

    private static (HttpMessageInvoker Client, RecordingHandler Inner) Guarded(ModulePermissionGate gate)
    {
        var inner = new RecordingHandler();
        return (new HttpMessageInvoker(new ModuleHttpGuard(gate, _module) { InnerHandler = inner }), inner);
    }

    [Theory]
    [InlineData("https://api.example.com/v1/today")]
    [InlineData("https://API.Example.com:8443/")]
    [InlineData("http://api.example.com/")]
    public async Task A_request_to_a_granted_host_is_sent(string address)
    {
        var (gate, sink) = Gate("network:api.example.com");
        var (client, inner) = Guarded(gate);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, address), Ct);

        Assert.Single(inner.Sent);
        Assert.Empty(sink.Denials);
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("https://evil.api.example.com/")]
    [InlineData("https://api.example.com.evil.test/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://2130706433/")]
    [InlineData("https://0x7f.0.0.1/")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://localhost/")]
    public async Task A_request_to_any_other_host_is_refused_before_it_is_sent(string address)
    {
        var (gate, sink) = Gate("network:api.example.com");
        var (client, inner) = Guarded(gate);

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, address), Ct));

        Assert.Empty(inner.Sent);
        Assert.NotEmpty(sink.Denials);
    }

    [Theory]
    [InlineData("https://user:pw@api.example.com/")]
    [InlineData("https://evil.test@api.example.com/")]
    [InlineData("https://api.example.com@evil.test/")]
    [InlineData("ftp://api.example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/relative")]
    public async Task Credentials_in_the_address_other_schemes_and_relative_addresses_are_refused(string address)
    {
        var (gate, _) = Gate("network:api.example.com");
        var (client, inner) = Guarded(gate);

        await Assert.ThrowsAsync<ModuleNetworkRefusedException>(
            () => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(address, UriKind.RelativeOrAbsolute)), Ct));

        Assert.Empty(inner.Sent);
    }

    [Fact]
    public async Task A_host_header_cannot_front_for_another_host()
    {
        var (gate, _) = Gate("network:api.example.com");
        var (client, inner) = Guarded(gate);
        var fronted = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/");
        fronted.Headers.Host = "internal.example.com";
        var honest = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com:8443/");
        honest.Headers.Host = "api.example.com:8443";

        await Assert.ThrowsAsync<ModuleNetworkRefusedException>(() => client.SendAsync(fronted, Ct));
        using var response = await client.SendAsync(honest, Ct);

        Assert.Single(inner.Sent);
    }

    [Fact]
    public async Task An_internationalised_name_is_checked_in_the_form_that_is_dialled()
    {
        var (gate, _) = Gate("network:xn--bcher-kva.example");
        var (client, inner) = Guarded(gate);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://bücher.example/"), Ct);

        Assert.Single(inner.Sent);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::1")]
    [InlineData("100::1")]
    public void Addresses_inside_the_servers_own_network_are_not_public(string address) =>
        Assert.False(ModuleAddressRules.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:2800:220:1::1")]
    [InlineData("64:ff9b::5db8:d822")]
    public void Ordinary_internet_addresses_are_public(string address) =>
        Assert.True(ModuleAddressRules.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task A_granted_name_that_resolves_into_the_private_network_is_refused()
    {
        var (gate, _) = Gate("network:api.example.com");

        // DNS rebinding: the name the owner approved now answers with the metadata address.
        await Assert.ThrowsAsync<ModuleNetworkRefusedException>(async () => await ModuleHttp.VetAsync(
            gate, _module, (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("169.254.169.254")]), "api.example.com", Ct));

        // One private answer among public ones refuses the whole name.
        await Assert.ThrowsAsync<ModuleNetworkRefusedException>(async () => await ModuleHttp.VetAsync(
            gate, _module, (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34"), IPAddress.Loopback]), "api.example.com", Ct));

        await Assert.ThrowsAsync<ModuleNetworkRefusedException>(async () => await ModuleHttp.VetAsync(
            gate, _module, (_, _) => ValueTask.FromResult<IPAddress[]>([]), "api.example.com", Ct));
    }

    [Fact]
    public async Task A_granted_name_with_public_answers_and_a_granted_literal_pass()
    {
        var (gate, _) = Gate("network:api.example.com", "network:10.0.0.5");
        var resolved = 0;
        ModuleHostResolver resolver = (_, _) =>
        {
            resolved++;
            return ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")]);
        };

        Assert.Equal([IPAddress.Parse("93.184.216.34")], await ModuleHttp.VetAsync(gate, _module, resolver, "api.example.com", Ct));

        // The owner approved this private address by its literal, so it is used as written and never resolved.
        Assert.Equal([IPAddress.Parse("10.0.0.5")], await ModuleHttp.VetAsync(gate, _module, resolver, "10.0.0.5", Ct));
        Assert.Equal(1, resolved);
    }

    [Fact]
    public async Task The_connection_step_checks_the_gate_again_for_the_name_it_dials()
    {
        var (gate, _) = Gate("network:api.example.com");

        await Assert.ThrowsAsync<ModulePermissionDeniedException>(async () => await ModuleHttp.VetAsync(
            gate, _module, (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")]), "other.example.com", Ct));
    }

    [Fact]
    public async Task The_real_stack_reaches_a_granted_literal_and_does_not_follow_a_redirect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(
            async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(Ct);
                var stream = peer.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer, Ct);
                var reply = "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), Ct);
            },
            Ct);
        var (gate, sink) = Gate("network:127.0.0.1");
        using var client = new HttpClient(ModuleHttp.CreateHandler(gate, _module));

        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/"), Ct);
        await server;

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Empty(sink.Denials);
    }

    [Fact]
    public async Task The_real_stack_refuses_loopback_reached_through_a_granted_name()
    {
        var (gate, _) = Gate("network:rebind.example.com");
        using var client = new HttpClient(ModuleHttp.CreateHandler(
            gate, _module, (_, _) => ValueTask.FromResult<IPAddress[]>([IPAddress.Loopback])));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(new Uri("http://rebind.example.com:9/"), Ct));

        Assert.Contains(Chain(error), e => e is ModuleNetworkRefusedException);
    }

    private static IEnumerable<Exception> Chain(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }
}
