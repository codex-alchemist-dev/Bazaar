using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using Bazaar.Conduit.Cloudflared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Conduit.LiveTests;

/// <summary>Settings backed by a dictionary. Missing keys return the default.</summary>
internal sealed class MapSettings(IReadOnlyDictionary<string, object> values) : IConduitSettings
{
    public T Read<T>(string key, T defaultValue) =>
        values.TryGetValue(key, out var v) && v is T typed ? typed : defaultValue;
}

/// <summary>
/// Opens a real Cloudflare quick tunnel (no account) through <see cref="CloudflaredModule"/> and the production
/// <see cref="SystemCloudflaredLauncher"/>, then talks to a local echo server through it. Retries the tunnel start,
/// retries the first WebSocket connect (a new quick tunnel takes a few seconds to route), and measures round trips.
/// Doc 39 section 2 (<c>cloudflared</c>, quick mode) and its testing section.
/// </summary>
public static class LiveTunnelHarness
{
    /// <summary>Opt-in switch. Hosted CI never sets it, so the live test is skipped there.</summary>
    public const string EnableVariable = "BAZAAR_CONDUIT_LIVE";

    /// <summary>Path of the <c>cloudflared</c> binary. Defaults to <c>cloudflared</c> on PATH.</summary>
    public const string BinaryVariable = "BAZAAR_CONDUIT_CLOUDFLARED";

    /// <summary>Where to write the Markdown report, if set.</summary>
    public const string ReportVariable = "BAZAAR_CONDUIT_REPORT";

    private const string _edgeHost = "region1.v2.argotunnel.com";
    private const int _edgePort = 7844;
    private const string _keyHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>The binary to run.</summary>
    public static string Binary => Environment.GetEnvironmentVariable(BinaryVariable) is { Length: > 0 } b ? b : "cloudflared";

    /// <summary>Returns why the live test cannot run here, or null when it can.</summary>
    public static async Task<string?> SkipReasonAsync()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            return $"Set {EnableVariable}=1 to run the live tunnel test.";
        }

        if (await VersionAsync().ConfigureAwait(false) is null)
        {
            return $"cloudflared not runnable ({Binary}). Install it or set {BinaryVariable}.";
        }

        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(_edgeHost, _edgePort, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return $"Cloudflare edge {_edgeHost}:{_edgePort} is not reachable (offline or egress blocked).";
        }

        return null;
    }

    /// <summary>Runs <c>cloudflared --version</c>; returns its first line or null when it cannot run.</summary>
    public static async Task<string?> VersionAsync()
    {
        try
        {
            var info = new ProcessStartInfo(Binary, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var text = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0 ? text.Trim().Split('\n')[0] : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Runs the full proof. Throws when no echo came back after every retry.</summary>
    public static async Task<LiveTunnelReport> RunAsync(int pings, int tunnelAttempts, TimeSpan offerTimeout, CancellationToken cancellationToken)
    {
        var version = await VersionAsync().ConfigureAwait(false) ?? "unknown";
        await using var echo = new EchoServer();
        var settings = new MapSettings(new Dictionary<string, object>
        {
            ["conduit.cloudflared.mode"] = "quick",
            ["conduit.cloudflared.binary_path"] = Binary,
        });
        var context = new ConduitContext(echo.Port, _keyHash, settings, TimeProvider.System, NullLogger.Instance);
        var failures = new List<string>();

        for (var attempt = 1; attempt <= tunnelAttempts; attempt++)
        {
            var module = new CloudflaredModule(new SystemCloudflaredLauncher());
            var offerReady = new TaskCompletionSource<ConduitOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
            module.StatusChanged += (_, status) =>
            {
                if (status is { State: ConduitModuleState.Running, Offers: [var first, ..] })
                {
                    offerReady.TrySetResult(first);
                }
            };

            var clock = Stopwatch.StartNew();
            await module.StartAsync(context, cancellationToken).ConfigureAwait(false);
            try
            {
                var offer = await offerReady.Task.WaitAsync(offerTimeout, cancellationToken).ConfigureAwait(false);
                var timeToOffer = clock.Elapsed;
                return await MeasureAsync(offer, version, attempt, timeToOffer, clock, pings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or WebSocketException or InvalidOperationException)
            {
                failures.Add($"attempt {attempt}: {ex.GetType().Name}: {module.Status.State} {module.Status.Reason}");
            }
            finally
            {
                await module.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("No echo through the tunnel after every retry. " + string.Join("; ", failures));
    }

    private static async Task<LiveTunnelReport> MeasureAsync(
        ConduitOffer offer, string version, int tunnelAttempt, TimeSpan timeToOffer, Stopwatch clock, int pings, CancellationToken cancellationToken)
    {
        ClientWebSocket? socket = null;
        var connectAttempts = 0;
        while (socket is null)
        {
            connectAttempts++;
            var candidate = new ClientWebSocket();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await candidate.ConnectAsync(offer.Uri, timeout.Token).ConfigureAwait(false);
                socket = candidate;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                candidate.Dispose();
                if (connectAttempts >= 12)
                {
                    throw new InvalidOperationException($"WebSocket connect failed {connectAttempts} times: {ex.Message}", ex);
                }

                // A fresh quick tunnel answers 404 or 530 until its route reaches the edge; back off and retry.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(connectAttempts * 2, 10)), cancellationToken).ConfigureAwait(false);
            }
        }

        using (socket)
        {
            var samples = new List<TimeSpan>(pings);
            TimeSpan? firstEcho = null;
            var buffer = new byte[256];
            for (var i = 0; i < pings; i++)
            {
                var payload = System.Text.Encoding.UTF8.GetBytes($"ping-{i}");
                var sent = Stopwatch.StartNew();
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
                var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                sent.Stop();
                if (result.Count == payload.Length && buffer.AsSpan(0, result.Count).SequenceEqual(payload))
                {
                    samples.Add(sent.Elapsed);
                    firstEcho ??= clock.Elapsed;
                }
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
            return new LiveTunnelReport(
                "cloudflared quick tunnel", version, tunnelAttempt, timeToOffer, firstEcho ?? clock.Elapsed, connectAttempts,
                pings, samples.Count, samples.Count > 0 ? LatencyStats.From(samples) : null, offer.TerminatesTls);
        }
    }
}
