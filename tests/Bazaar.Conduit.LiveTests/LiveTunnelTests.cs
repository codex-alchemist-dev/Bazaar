namespace Bazaar.Conduit.LiveTests;

/// <summary>Offline arithmetic tests plus the live tunnel proof, which skips itself when it cannot run.</summary>
public sealed class LiveTunnelTests
{
    [Fact]
    public void Stats_use_nearest_rank_percentiles()
    {
        var samples = Enumerable.Range(1, 100).Select(i => TimeSpan.FromMilliseconds(i)).Reverse().ToArray();
        var stats = LatencyStats.From(samples);

        Assert.Equal(100, stats.Count);
        Assert.Equal(1, stats.MinMs);
        Assert.Equal(50, stats.P50Ms);
        Assert.Equal(95, stats.P95Ms);
        Assert.Equal(100, stats.MaxMs);
        Assert.Equal(50.5, stats.MeanMs);
    }

    [Fact]
    public void Stats_reject_an_empty_sample() =>
        Assert.Throws<ArgumentException>(() => LatencyStats.From([]));

    [Fact]
    public void Report_renders_without_the_tunnel_host()
    {
        var report = new LiveTunnelReport(
            "cloudflared quick tunnel", "cloudflared version 1.0", 1, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(9), 3, 20, 20,
            LatencyStats.From([TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(60)]), true);

        var text = report.ToMarkdown();

        Assert.Contains("Echoes | 20 of 20", text);
        Assert.Contains("TerminatesTls | True", text);
        Assert.DoesNotContain("trycloudflare", text);
    }

    [Fact]
    public async Task Echo_server_round_trips_a_message_locally()
    {
        await using var server = new EchoServer();
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), TestContext.Current.CancellationToken);

        var payload = "hello"u8.ToArray();
        await socket.SendAsync(payload, System.Net.WebSockets.WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);
        var buffer = new byte[16];
        var result = await socket.ReceiveAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(payload, buffer.AsSpan(0, result.Count).ToArray());
    }

    [Fact]
    public async Task Cloudflared_quick_tunnel_carries_a_websocket_end_to_end()
    {
        var reason = await LiveTunnelHarness.SkipReasonAsync();
        Assert.SkipUnless(reason is null, reason ?? string.Empty);

        var report = await LiveTunnelHarness.RunAsync(
            pings: 20, tunnelAttempts: 3, offerTimeout: TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        if (Environment.GetEnvironmentVariable(LiveTunnelHarness.ReportVariable) is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, report.ToMarkdown(), TestContext.Current.CancellationToken);
        }

        Assert.Equal(report.Sent, report.Echoed);
        Assert.True(report.TerminatesTls, "A quick tunnel offer must set TerminatesTls (doc 39 section 3).");
    }
}
