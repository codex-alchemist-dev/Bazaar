// Tests pass no CancellationToken to fake-clock and loopback calls; the harness applies its own timeouts.
#pragma warning disable xUnit1051
using System.Net.WebSockets;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Tests.Wire.Harness;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Tests.Wire;

/// <summary>Connection manager (W3) through the protocol harness (PRH).</summary>
public sealed class ConnectionTests
{
    [Fact]
    public async Task Header_credential_gets_a_welcome()
    {
        var principal = new PrincipalRef(Guid.NewGuid(), "human");
        await using var server = await WireTestServer.StartAsync(auth: new FakeAuthenticator().Allow("good", principal));
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, "good");

        var reply = await client.HelloAsync(WireTestClient.DefaultHello());

        Assert.Equal(WireTypes.Welcome, reply.T);
        Assert.Equal(EnvelopeKind.Response, reply.K);
        Assert.NotNull(reply.Re);
        var welcome = client.Codec.DecodePayload<Welcome>(reply);
        Assert.Equal(1, welcome.Version);
        Assert.Equal("json", welcome.Codec);
        Assert.Equal(principal.Id, welcome.PrincipalId);
        Assert.Equal(20, welcome.HeartbeatIntervalS);
        Assert.Equal(60, welcome.HeartbeatTimeoutS);
        Assert.Equal(256 * 1024, welcome.Limits.MaxFrameBytes);
        Assert.Equal(32, welcome.Limits.InflightRequestsMax);
        Assert.Null(welcome.UpgradeRequired);
    }

    [Fact]
    public async Task Credential_in_hello_is_accepted()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri);

        var reply = await client.HelloAsync(WireTestClient.DefaultHello(credential: "good"));

        Assert.Equal(WireTypes.Welcome, reply.T);
    }

    [Fact]
    public async Task Bad_credential_closes_with_4401()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, "wrong");
        await client.SendAsync(client.RequestOf(WireTypes.Hello, WireTestClient.DefaultHello()));

        Assert.Equal(WireCloseCodes.AuthFailed, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Missing_credential_closes_with_4401()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri);
        await client.SendAsync(client.RequestOf(WireTypes.Hello, WireTestClient.DefaultHello()));

        Assert.Equal(WireCloseCodes.AuthFailed, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Silence_after_connect_closes_with_4408_at_five_seconds()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, "good");
        var closed = client.WaitForCloseAsync();

        await server.AdvanceUntilAsync(() => closed.IsCompleted, TimeSpan.FromSeconds(30));

        Assert.Equal(WireCloseCodes.HelloTimeout, await closed);
    }

    [Fact]
    public async Task Garbage_first_frame_closes_with_4400()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, "good");
        await client.SendRawAsync("not json"u8.ToArray(), WebSocketMessageType.Text);

        Assert.Equal(WireCloseCodes.BadFrame, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task First_frame_other_than_hello_closes_with_4400()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, "good");
        await client.SendAsync(client.RequestOf(WireTypes.Ping, new Ping(null)));

        Assert.Equal(WireCloseCodes.BadFrame, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task No_version_overlap_answers_upgrade_required_then_closes_4426()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, "good");

        var reply = await client.HelloAsync(WireTestClient.DefaultHello(min: 2, max: 3));

        var welcome = client.Codec.DecodePayload<Welcome>(reply);
        Assert.True(welcome.UpgradeRequired);
        Assert.Equal("0.1.0", welcome.MinimumBuild);
        Assert.Equal(WireCloseCodes.UpgradeRequired, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Previous_major_is_refused_when_the_operator_turns_it_off()
    {
        await using var server = await WireTestServer.StartAsync(o => o.PreviousMajorSupported = false);
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, "good");

        var reply = await client.HelloAsync(WireTestClient.DefaultHello(min: 0, max: 0));

        Assert.True(client.Codec.DecodePayload<Welcome>(reply).UpgradeRequired);
        Assert.Equal(WireCloseCodes.UpgradeRequired, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Previous_major_gets_a_welcome_by_default()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, "good");

        var reply = await client.HelloAsync(WireTestClient.DefaultHello(min: 0, max: 0));

        Assert.Equal(0, client.Codec.DecodePayload<Welcome>(reply).Version);
    }

    [Fact]
    public async Task Unknown_codec_is_treated_as_no_overlap()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, "good");

        var reply = await client.HelloAsync(WireTestClient.DefaultHello(codecs: ["protobuf"]));

        Assert.True(client.Codec.DecodePayload<Welcome>(reply).UpgradeRequired);
    }

    [Fact]
    public async Task Ping_request_is_answered_with_pong_echoing_the_nonce()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = await WireTestClient.ConnectedAsync(server);
        var ping = client.RequestOf(WireTypes.Ping, new Ping("abc"));
        await client.SendAsync(ping);

        var reply = await client.ReceiveAsync();

        Assert.Equal(EnvelopeKind.Response, reply.K);
        Assert.Equal(ping.Id, reply.Re);
        Assert.Equal("abc", client.Codec.DecodePayload<Pong>(reply).Nonce);
    }

    [Fact]
    public async Task Unknown_request_type_gets_err_unsupported()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = await WireTestClient.ConnectedAsync(server);
        var request = client.RequestOf("nope.nothing", new Ping(null));
        await client.SendAsync(request);

        var reply = await client.ReceiveAsync();

        Assert.Equal(EnvelopeKind.Error, reply.K);
        Assert.Equal(request.Id, reply.Re);
        Assert.Equal(WireErrorCodes.Unsupported, client.Codec.DecodePayload<WireError>(reply).Code);
    }

    [Fact]
    public async Task Request_without_an_id_gets_err_bad_request()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = await WireTestClient.ConnectedAsync(server);
        await client.SendAsync(client.RequestOf(WireTypes.Ping, new Ping(null)) with { Id = null });

        var reply = await client.ReceiveAsync();

        Assert.Equal(WireErrorCodes.BadRequest, client.Codec.DecodePayload<WireError>(reply).Code);
    }

    [Fact]
    public async Task Handler_that_throws_gets_err_internal_and_the_socket_survives()
    {
        var boom = new DelegateHandler("boom", (_, _) => throw new InvalidOperationException("secret detail"));
        await using var server = await WireTestServer.StartAsync(handlers: [boom]);
        await using var client = await WireTestClient.ConnectedAsync(server);
        await client.SendAsync(client.RequestOf("boom", new Ping(null)));

        var error = client.Codec.DecodePayload<WireError>(await client.ReceiveAsync());

        Assert.Equal(WireErrorCodes.Internal, error.Code);
        Assert.DoesNotContain("secret", error.Message ?? string.Empty, StringComparison.Ordinal);
        await client.SendAsync(client.RequestOf(WireTypes.Ping, new Ping("still here")));
        Assert.Equal("still here", client.Codec.DecodePayload<Pong>(await client.ReceiveAsync()).Nonce);
    }

    [Fact]
    public async Task Handler_payload_that_does_not_fit_gets_err_bad_request()
    {
        var wants = new DelegateHandler("wants.hello", (c, _) => ValueTask.FromResult(WireReply.Ok(c.Connection.Codec, c.Payload<Hello>())));
        await using var server = await WireTestServer.StartAsync(handlers: [wants]);
        await using var client = await WireTestClient.ConnectedAsync(server);
        await client.SendAsync(client.RequestOf("wants.hello", new Ping(null)) with { P = "123"u8.ToArray() });

        var reply = await client.ReceiveAsync();

        Assert.Equal(WireErrorCodes.BadRequest, client.Codec.DecodePayload<WireError>(reply).Code);
    }

    [Fact]
    public async Task Garbage_frame_after_handshake_closes_with_4400()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = await WireTestClient.ConnectedAsync(server);
        await client.SendRawAsync("{broken"u8.ToArray(), WebSocketMessageType.Text);

        Assert.Equal(WireCloseCodes.BadFrame, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Binary_frame_on_a_json_connection_closes_with_4400()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = await WireTestClient.ConnectedAsync(server);
        await client.SendRawAsync("{}"u8.ToArray(), WebSocketMessageType.Binary);

        Assert.Equal(WireCloseCodes.BadFrame, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Oversize_frame_closes_with_4400()
    {
        await using var server = await WireTestServer.StartAsync(o => o.MaxFrameKib = 1);
        await using var client = await WireTestClient.ConnectedAsync(server);
        await client.SendRawAsync(new byte[2048], WebSocketMessageType.Text);

        Assert.Equal(WireCloseCodes.BadFrame, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Requests_beyond_the_in_flight_limit_get_rate_limited()
    {
        var gate = new TaskCompletionSource();
        var slow = new DelegateHandler("slow", async (_, _) =>
        {
            await gate.Task;
            return WireReply.Ok();
        });
        await using var server = await WireTestServer.StartAsync(o => o.InflightRequestsMax = 2, handlers: [slow]);
        await using var client = await WireTestClient.ConnectedAsync(server);
        for (var i = 0; i < 3; i++) await client.SendAsync(client.RequestOf("slow", new Ping(null)));

        var limited = await client.ReceiveAsync();

        Assert.Equal(EnvelopeKind.Error, limited.K);
        var error = client.Codec.DecodePayload<WireError>(limited);
        Assert.Equal(WireErrorCodes.RateLimited, error.Code);
        Assert.True(error.RetryAfterMs > 0);
        gate.SetResult();
        Assert.Equal(EnvelopeKind.Response, (await client.ReceiveAsync()).K);
        Assert.Equal(EnvelopeKind.Response, (await client.ReceiveAsync()).K);
    }

    [Fact]
    public async Task Sustained_rate_limiting_closes_with_4429()
    {
        await using var server = await WireTestServer.StartAsync(o =>
        {
            o.RequestBurst = 1;
            o.RequestsPerSecond = 1;
            o.RateLimitCloseAfter = 3;
        });
        await using var client = await WireTestClient.ConnectedAsync(server);
        for (var i = 0; i < 6; i++) await client.SendAsync(client.RequestOf(WireTypes.Ping, new Ping(null)));

        Assert.Equal(WireCloseCodes.RateLimited, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Server_pings_every_interval_and_any_frame_keeps_the_socket_alive()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = await WireTestClient.ConnectedAsync(server);

        var next = client.TryReceiveAsync();
        await server.AdvanceUntilAsync(() => next.IsCompleted, TimeSpan.FromSeconds(25));
        var ping = await next;
        Assert.Equal(WireTypes.Ping, ping!.Value.T);
        Assert.Equal(EnvelopeKind.Event, ping.Value.K);

        // Keep talking for longer than the timeout: the socket must stay open.
        for (var i = 0; i < 4; i++)
        {
            server.Clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(30);
            await client.SendAsync(client.RequestOf(WireTypes.Ping, new Ping(null)));
            await Task.Delay(30);
        }

        Assert.Null(client.CloseCode);
    }

    [Fact]
    public async Task Silent_client_is_closed_after_the_heartbeat_timeout()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = await WireTestClient.ConnectedAsync(server);
        var closed = client.WaitForCloseAsync();

        await server.AdvanceUntilAsync(() => closed.IsCompleted, TimeSpan.FromSeconds(90));

        Assert.True(closed.IsCompleted);
        Assert.Equal(1001, await closed);
    }

    [Fact]
    public async Task Expired_credential_closes_with_4401()
    {
        var clock = new Bazaar.Testing.FakeClock();
        var auth = new FakeAuthenticator().Allow("short", expiresAt: clock.GetUtcNow().AddSeconds(30));
        await using var server = await WireTestServer.StartAsync(auth: auth, clock: clock);
        await using var client = await WireTestClient.ConnectedAsync(server, credential: "short");
        var closed = client.WaitForCloseAsync();

        await server.AdvanceUntilAsync(() => closed.IsCompleted, TimeSpan.FromSeconds(60));

        Assert.Equal(WireCloseCodes.AuthFailed, await closed);
    }

    [Fact]
    public async Task Newest_socket_wins_when_a_device_exceeds_its_limit()
    {
        await using var server = await WireTestServer.StartAsync(o => o.MaxSocketsPerDevice = 2);
        await using var first = await WireTestClient.ConnectedAsync(server);
        await using var second = await WireTestClient.ConnectedAsync(server);
        await using var third = await WireTestClient.ConnectedAsync(server);

        Assert.Equal(1008, await first.WaitForCloseAsync());
        Assert.Equal(2, server.Manager.Registry.ForDevice("device-1").Count);
        await second.SendAsync(second.RequestOf(WireTypes.Ping, new Ping(null)));
        Assert.Equal(EnvelopeKind.Response, (await second.ReceiveAsync()).K);
    }

    [Fact]
    public async Task Sockets_of_other_devices_do_not_count_against_the_limit()
    {
        await using var server = await WireTestServer.StartAsync(o => o.MaxSocketsPerDevice = 1);
        await using var a = await WireTestClient.ConnectedAsync(server, "device-a");
        await using var b = await WireTestClient.ConnectedAsync(server, "device-b");

        Assert.Equal(2, server.Manager.Registry.Connections.Count);
    }

    [Fact]
    public async Task Revoking_a_device_closes_its_socket_with_4403_at_once()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = await WireTestClient.ConnectedAsync(server);

        await server.Manager.Registry.CloseDeviceAsync("device-1", WireCloseCodes.Revoked, "revoked");

        Assert.Equal(WireCloseCodes.Revoked, await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Revoking_a_principal_closes_all_their_sockets()
    {
        var principal = new PrincipalRef(Guid.NewGuid(), "human");
        await using var server = await WireTestServer.StartAsync(auth: new FakeAuthenticator().Allow("good", principal));
        await using var a = await WireTestClient.ConnectedAsync(server, "device-a");
        await using var b = await WireTestClient.ConnectedAsync(server, "device-b");

        await server.Manager.Registry.ClosePrincipalAsync(principal.Id, WireCloseCodes.Revoked, "revoked");

        Assert.Equal(WireCloseCodes.Revoked, await a.WaitForCloseAsync());
        Assert.Equal(WireCloseCodes.Revoked, await b.WaitForCloseAsync());
    }

    [Fact]
    public async Task Closed_connections_leave_the_registry()
    {
        await using var server = await WireTestServer.StartAsync();
        var client = await WireTestClient.ConnectedAsync(server);
        Assert.Single(server.Manager.Registry.Connections);

        await server.Manager.Registry.CloseDeviceAsync("device-1", WireCloseCodes.Revoked, "bye");
        await client.WaitForCloseAsync();
        for (var i = 0; i < 100 && server.Manager.Registry.Connections.Count > 0; i++) await Task.Delay(20);

        Assert.Empty(server.Manager.Registry.Connections);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Event_queue_overflow_is_reported_and_lagged_still_gets_through()
    {
        await using var server = await WireTestServer.StartAsync(o => o.OutboundQueueMax = 5);
        await using var client = await WireTestClient.ConnectedAsync(server);
        var connection = server.Manager.Registry.ForDevice("device-1").Single();
        var evt = new Envelope(1, WireTypes.GcMessage, EnvelopeKind.Event, null, null, "gc:x", 1, null, default);

        var refused = 0;
        for (var i = 0; i < 20000; i++)
        {
            if (!connection.TryEnqueueEvent(evt)) refused++;
        }

        Assert.True(refused > 0);
        Assert.True(connection.TryEnqueueLagged(new Lagged("gc:x", 7)));
        Envelope? frame;
        Lagged? lagged = null;
        while ((frame = await client.TryReceiveAsync()) is { } f)
        {
            if (f.T == WireTypes.Lagged)
            {
                lagged = client.Codec.DecodePayload<Lagged>(f);
                break;
            }
        }

        Assert.Equal(7, lagged?.LastSent);
    }

    [Fact]
    public async Task Hello_cursors_reach_the_connection()
    {
        await using var server = await WireTestServer.StartAsync();
        await using var client = new WireTestClient();
        await client.ConnectAsync(server.SocketUri, "good");
        var hello = WireTestClient.DefaultHello() with { Cursors = [new StreamCursor("gc:x", 4, 2)] };

        await client.HelloAsync(hello);

        var connection = server.Manager.Registry.ForDevice("device-1").Single();
        Assert.Equal(new StreamCursor("gc:x", 4, 2), connection.HelloCursors.Single());
    }
}
