using System.Text;
using Bazaar.Client.Transport.Grpc.Generated;
using Bazaar.Wire.Abstractions;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;

namespace Bazaar.Transport.Grpc.Tests;

public class GrpcTransportTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static CancellationToken Token() => new CancellationTokenSource(Wait).Token;

    [Fact]
    public async Task Echoes_frames_over_connect_and_reports_the_connector_as_supported()
    {
        await using var host = await TestHost.StartAsync();
        var connector = host.Connector();
        Assert.True(connector.IsSupported);
        Assert.Equal("grpc", connector.Transport);

        await using var connection = await connector.ConnectAsync(TestHost.Offer, ["json"], Token());
        foreach (var text in new[] { "one", "two", "three" })
        {
            await connection.SendAsync(Encoding.ASCII.GetBytes(text), Token());
            var echo = await connection.ReceiveAsync(Token());
            Assert.False(echo.IsClosed);
            Assert.Equal(text, Encoding.ASCII.GetString(echo.Frame.Span));
        }

        Assert.Equal(new WireProfile("grpc", "json"), connection.Profile);
    }

    [Fact]
    public async Task Codec_metadata_picks_the_first_codec_both_sides_have()
    {
        await using var host = await TestHost.StartAsync();
        await using var connection = await host.Connector().ConnectAsync(TestHost.Offer, ["msgpack", "cbor", "json"], Token());
        Assert.Equal("cbor", connection.Profile.Codec);
        await connection.SendAsync(new byte[] { 1 }, Token());
        await connection.ReceiveAsync(Token());
        Assert.Equal("cbor", Assert.Single(host.Handoff.Profiles).Codec);
    }

    [Fact]
    public async Task No_codec_metadata_means_the_floor_codec()
    {
        await using var host = await TestHost.StartAsync();
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        using var call = new Session.SessionClient(channel).Connect(new Metadata { { "authorization", "Bearer " + FakeAuthenticator.Good } }, cancellationToken: Token());
        var headers = await call.ResponseHeadersAsync;
        Assert.Equal("json", headers.GetValue("bazaar-codec"));
    }

    [Fact]
    public async Task Content_type_suffix_names_the_codec_when_no_metadata_does()
    {
        await using var host = await TestHost.StartAsync();
        using var client = new HttpClient(host.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/bazaar.wire.v1.Session/Connect")
        {
            Version = new Version(2, 0),
            Content = new ByteArrayContent([0, 0, 0, 0, 0]),
        };
        request.Content.Headers.ContentType = new("application/grpc+cbor");
        request.Headers.TryAddWithoutValidation("authorization", "Bearer " + FakeAuthenticator.Good);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token());
        Assert.True(response.Headers.TryGetValues("bazaar-codec", out var values));
        Assert.Equal("cbor", Assert.Single(values));
    }

    [Fact]
    public async Task A_codec_the_server_does_not_have_is_refused()
    {
        await using var host = await TestHost.StartAsync();
        var ex = await Assert.ThrowsAsync<IOException>(async () => await host.Connector().ConnectAsync(TestHost.Offer, ["msgpack"], Token()));
        Assert.Equal(StatusCode.InvalidArgument, Assert.IsType<RpcException>(ex.InnerException).StatusCode);
        Assert.Empty(host.Handoff.Profiles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Missing_or_wrong_credential_is_refused_before_the_hand_off(string? credential)
    {
        await using var host = await TestHost.StartAsync();
        var ex = await Assert.ThrowsAsync<IOException>(async () => await host.Connector(credential).ConnectAsync(TestHost.Offer, ["json"], Token()));
        var rpc = Assert.IsType<RpcException>(ex.InnerException);
        Assert.Equal(StatusCode.Unauthenticated, rpc.StatusCode);
        Assert.Equal("4401", rpc.Trailers.GetValue("bazaar-close-code"));
        Assert.Empty(host.Handoff.Profiles);
        if (credential is null) Assert.Empty(host.Auth.Calls);
    }

    [Fact]
    public async Task A_message_over_the_gRPC_size_limit_is_refused_and_never_reaches_the_hand_off()
    {
        await using var host = await TestHost.StartAsync(o => o.MaxFrameBytes = 1024);
        await using var connection = await host.Connector(maxFrameBytes: 64 * 1024).ConnectAsync(TestHost.Offer, ["json"], Token());
        await connection.SendAsync(new byte[8 * 1024], Token());
        var received = await connection.ReceiveAsync(Token());
        Assert.True(received.IsClosed);
        Assert.Equal(4400, received.CloseCode);
        Assert.Empty(host.Handoff.Received);
    }

    [Fact]
    public async Task A_message_just_over_the_frame_limit_is_refused_by_the_size_check()
    {
        await using var host = await TestHost.StartAsync(o => o.MaxFrameBytes = 1024);
        await using var connection = await host.Connector(maxFrameBytes: 64 * 1024).ConnectAsync(TestHost.Offer, ["json"], Token());
        await connection.SendAsync(new byte[1025], Token());
        var received = await connection.ReceiveAsync(Token());
        Assert.Equal(4400, received.CloseCode);
        Assert.Empty(host.Handoff.Received);
    }

    [Fact]
    public async Task A_frame_at_the_limit_passes()
    {
        await using var host = await TestHost.StartAsync(o => o.MaxFrameBytes = 1024);
        await using var connection = await host.Connector().ConnectAsync(TestHost.Offer, ["json"], Token());
        await connection.SendAsync(new byte[1024], Token());
        var echo = await connection.ReceiveAsync(Token());
        Assert.Equal(1024, echo.Frame.Length);
    }

    [Fact]
    public async Task The_client_refuses_to_send_over_its_own_limit()
    {
        await using var host = await TestHost.StartAsync();
        await using var connection = await host.Connector(maxFrameBytes: 100).ConnectAsync(TestHost.Offer, ["json"], Token());
        await Assert.ThrowsAsync<ArgumentException>(async () => await connection.SendAsync(new byte[101], Token()));
    }

    [Fact]
    public async Task Close_code_travels_in_the_trailers()
    {
        await using var host = await TestHost.StartAsync();
        await using var connection = await host.Connector().ConnectAsync(TestHost.Offer, ["json"], Token());
        await connection.SendAsync(Encoding.ASCII.GetBytes("close"), Token());
        var received = await connection.ReceiveAsync(Token());
        Assert.True(received.IsClosed);
        Assert.Equal(4403, received.CloseCode);
    }

    [Fact]
    public async Task Native_connect_is_unavailable_when_grpc_is_not_enabled()
    {
        await using var host = await TestHost.StartAsync(enabled: o => o.Enabled = [WireNames.GrpcWeb]);
        var ex = await Assert.ThrowsAsync<IOException>(async () => await host.Connector().ConnectAsync(TestHost.Offer, ["json"], Token()));
        Assert.Equal(StatusCode.Unimplemented, Assert.IsType<RpcException>(ex.InnerException).StatusCode);
    }

    // ---- gRPC-Web ----

    private static Session.SessionClient WebClient(TestHost host)
    {
        var handler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, host.Server.CreateHandler());
        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        return new Session.SessionClient(channel);
    }

    private static Metadata WebHeaders(string credential, string? session = null)
    {
        var headers = new Metadata { { "authorization", "Bearer " + credential }, { "bazaar-codec", "cbor,json" } };
        if (session is not null) headers.Add("bazaar-session", session);
        return headers;
    }

    private static Task<TestHost> WebEnabledHost() =>
        TestHost.StartAsync(enabled: o => o.Enabled = [WireNames.Grpc, WireNames.GrpcWeb]);

    [Fact]
    public async Task Grpc_web_subscribe_and_send_map_onto_one_connection()
    {
        await using var host = await WebEnabledHost();
        var client = WebClient(host);
        const string session = "session-0123456789abcdef";
        using var subscribe = client.Subscribe(new SubscribeRequest { SessionId = session }, WebHeaders(FakeAuthenticator.Good), cancellationToken: Token());
        var headers = await subscribe.ResponseHeadersAsync;
        Assert.Equal("cbor", headers.GetValue("bazaar-codec"));

        foreach (var text in new[] { "alpha", "beta" })
        {
            var ack = await client.SendAsync(new Frame { Data = ByteString.CopyFromUtf8(text) }, WebHeaders(FakeAuthenticator.Good, session), cancellationToken: Token());
            Assert.Empty(ack.Data);
            Assert.True(await subscribe.ResponseStream.MoveNext(Token()));
            Assert.Equal(text, subscribe.ResponseStream.Current.Data.ToStringUtf8());
        }

        var profile = Assert.Single(host.Handoff.Profiles);
        Assert.Equal(new WireProfile("grpc-web", "cbor"), profile);

        await client.SendAsync(new Frame { Data = ByteString.CopyFromUtf8("close") }, WebHeaders(FakeAuthenticator.Good, session), cancellationToken: Token());
        Assert.False(await subscribe.ResponseStream.MoveNext(Token()));
        Assert.Equal("4403", subscribe.GetTrailers().GetValue("bazaar-close-code"));
    }

    [Fact]
    public async Task Grpc_web_is_off_by_default()
    {
        await using var host = await TestHost.StartAsync();

        // Browser protocol: the translation is not switched on for the endpoint, so the call is refused outright.
        var web = WebClient(host);
        using var subscribe = web.Subscribe(new SubscribeRequest { SessionId = "session-0123456789abcdef" }, WebHeaders(FakeAuthenticator.Good), cancellationToken: Token());
        await Assert.ThrowsAnyAsync<RpcException>(async () => await subscribe.ResponseStream.MoveNext(Token()));

        // Native gRPC caller reaching the web methods: the service refuses them.
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        using var native = new Session.SessionClient(channel).Subscribe(new SubscribeRequest { SessionId = "session-0123456789abcdef" }, WebHeaders(FakeAuthenticator.Good), cancellationToken: Token());
        var ex = await Assert.ThrowsAnyAsync<RpcException>(async () => await native.ResponseStream.MoveNext(Token()));
        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
        Assert.Empty(host.Handoff.Profiles);
    }

    [Fact]
    public async Task Grpc_web_subscribe_without_credential_is_refused()
    {
        await using var host = await WebEnabledHost();
        var client = WebClient(host);
        using var subscribe = client.Subscribe(new SubscribeRequest { SessionId = "session-0123456789abcdef" }, new Metadata(), cancellationToken: Token());
        var ex = await Assert.ThrowsAnyAsync<RpcException>(async () => await subscribe.ResponseStream.MoveNext(Token()));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
        Assert.Empty(host.Handoff.Profiles);
    }

    [Fact]
    public async Task Grpc_web_send_needs_the_same_credential_and_a_known_session()
    {
        await using var host = await WebEnabledHost();
        var client = WebClient(host);
        const string session = "session-0123456789abcdef";
        using var subscribe = client.Subscribe(new SubscribeRequest { SessionId = session }, WebHeaders(FakeAuthenticator.Good), cancellationToken: Token());
        await subscribe.ResponseHeadersAsync;

        var wrong = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.SendAsync(new Frame { Data = ByteString.CopyFromUtf8("x") }, WebHeaders("wrong-token", session), cancellationToken: Token()));
        Assert.Equal(StatusCode.Unauthenticated, wrong.StatusCode);

        var unknown = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.SendAsync(new Frame { Data = ByteString.CopyFromUtf8("x") }, WebHeaders(FakeAuthenticator.Good, "session-does-not-exist"), cancellationToken: Token()));
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(host.Handoff.Received);
    }

    [Fact]
    public async Task Grpc_web_oversized_send_is_refused()
    {
        await using var host = await TestHost.StartAsync(o => o.MaxFrameBytes = 1024, o => o.Enabled = [WireNames.GrpcWeb]);
        var client = WebClient(host);
        const string session = "session-0123456789abcdef";
        using var subscribe = client.Subscribe(new SubscribeRequest { SessionId = session }, WebHeaders(FakeAuthenticator.Good), cancellationToken: Token());
        await subscribe.ResponseHeadersAsync;
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.SendAsync(new Frame { Data = ByteString.CopyFrom(new byte[4096]) }, WebHeaders(FakeAuthenticator.Good, session), cancellationToken: Token()));
        Assert.Equal(StatusCode.ResourceExhausted, ex.StatusCode);
        Assert.Empty(host.Handoff.Received);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces in the id 123456")]
    public async Task Grpc_web_rejects_a_malformed_session_id(string id)
    {
        await using var host = await WebEnabledHost();
        var client = WebClient(host);
        using var subscribe = client.Subscribe(new SubscribeRequest { SessionId = id }, WebHeaders(FakeAuthenticator.Good), cancellationToken: Token());
        var ex = await Assert.ThrowsAnyAsync<RpcException>(async () => await subscribe.ResponseStream.MoveNext(Token()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
