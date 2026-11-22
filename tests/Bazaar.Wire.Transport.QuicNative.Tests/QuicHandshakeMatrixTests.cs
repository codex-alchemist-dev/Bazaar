using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Bazaar.Client.Transport.QuicNative;
using Bazaar.Server.Transport.QuicNative;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Wire.Transport.QuicNative.Tests;

/// <summary>
/// DIAGNOSTIC ONLY. Runs raw <c>System.Net.Quic</c> loopback handshakes (no Bazaar code) over a matrix of ALPN ids, listener
/// bind addresses, client endpoint kinds, certificate recipes, ALPN-option placement, callback style and TargetHost, then one
/// run through the real <see cref="QuicWireListener"/> and <see cref="QuicWireConnector"/>. It records every outcome and prints
/// a table; it never fails because a handshake failed, so a broken combination cannot hide the rest. Read the table, not the
/// pass mark. Runs only with <c>BAZAAR_TEST_REAL_NET=1</c> and MsQuic present, like the other real-network tests.
/// </summary>
public sealed class QuicHandshakeMatrixTests(ITestOutputHelper output)
{
    private static readonly TimeSpan _caseTimeout = TimeSpan.FromSeconds(4);
    private static readonly string[] _alpns = ["bazaar-wire/1", "bazaar-wire-1", "bazaar-wire.1", "h3", "bazaar"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private enum Bind { Ipv4Loopback, Ipv6Loopback, Any }

    private enum Remote { IpEndPointMatching, DnsLocalhost, Dns127001 }

    private enum CertRecipe
    {
        /// <summary>Straight from CreateSelfSigned: ephemeral key, no persistence.</summary>
        EphemeralDirect,

        /// <summary>Pfx round-trip with EphemeralKeySet.</summary>
        Pkcs12Ephemeral,

        /// <summary>Pfx round-trip with UserKeySet (what TestCertificates.CreateLocalhost does).</summary>
        Pkcs12UserKeySet,

        /// <summary>UserKeySet plus PersistKeySet.</summary>
        Pkcs12UserPersist,

        /// <summary>UserKeySet but no SAN at all (CN=localhost only).</summary>
        Pkcs12UserNoSan,

        /// <summary>UserKeySet with SAN localhost, 127.0.0.1 and ::1.</summary>
        Pkcs12UserFullSan,
    }

    private sealed record Case(
        string Group,
        string ClientAlpn,
        string ServerAlpn,
        Bind Bind,
        Remote Remote,
        CertRecipe Cert,
        bool ServerAuthAlpn,
        bool AsyncCallback,
        string? TargetHost)
    {
        public string Describe() => string.Create(
            CultureInfo.InvariantCulture,
            $"cAlpn={ClientAlpn} sAlpn={ServerAlpn} bind={Bind} remote={Remote} cert={Cert} serverAuthAlpn={(ServerAuthAlpn ? "yes" : "no")} cb={(AsyncCallback ? "async" : "sync")} host={(TargetHost is null ? "(null)" : "'" + TargetHost + "'")}");
    }

    private sealed record Result(Case Case, bool Ok, string Client, string Server, string Negotiated, long Millis);

    [Fact]
    public async Task Handshake_matrix_records_every_outcome_and_never_fails()
    {
        Assert.SkipUnless(QuicNativeRealNetTests.Enabled, "Set BAZAAR_TEST_REAL_NET=1 and install MsQuic (libmsquic) to run.");

        var report = new StringBuilder();
        void Emit(string line)
        {
            report.AppendLine(line);
            output.WriteLine(line);
            Console.WriteLine(line);
        }

        PrintEnvironment(Emit);
        var results = new List<Result>();
        foreach (var testCase in BuildCases())
        {
            var result = await RunAsync(testCase, Ct);
            results.Add(result);
            Emit($"{(result.Ok ? "OK  " : "FAIL")} [{testCase.Group}] {testCase.Describe()} | {result.Millis} ms | negotiated={result.Negotiated} | client: {result.Client} | server: {result.Server}");
        }

        var bazaar = await RunBazaarAsync(Ct);
        Emit($"[bazaar-real] {bazaar}");

        Emit(string.Empty);
        Emit("=== SUMMARY (one row per case; OK means handshake completed on both sides) ===");
        Emit("group | ok/total");
        foreach (var group in results.GroupBy(static r => r.Case.Group))
        {
            Emit($"{group.Key} | {group.Count(static r => r.Ok)}/{group.Count()}");
        }

        Emit(string.Empty);
        Emit("--- failures grouped by client error (what to look for: which dimension all failing rows share) ---");
        foreach (var failure in results.Where(static r => !r.Ok).GroupBy(static r => FirstLine(r.Client)))
        {
            Emit($"{failure.Count()}x {failure.Key}");
            foreach (var row in failure.Take(60)) Emit($"    {row.Case.Group}: {row.Case.Describe()}");
        }

        Emit(string.Empty);
        Emit("--- dimension pivot: OK/total when one dimension is held at a value ---");
        Pivot(results, "client ALPN", static r => r.Case.ClientAlpn, Emit);
        Pivot(results, "listener bind", static r => r.Case.Bind.ToString(), Emit);
        Pivot(results, "client remote", static r => r.Case.Remote.ToString(), Emit);
        Pivot(results, "cert recipe", static r => r.Case.Cert.ToString(), Emit);
        Pivot(results, "server-auth ALPN set", static r => r.Case.ServerAuthAlpn.ToString(), Emit);
        Pivot(results, "callback", static r => r.Case.AsyncCallback ? "async" : "sync", Emit);
        Pivot(results, "TargetHost", static r => r.Case.TargetHost ?? "(null)", Emit);

        TryAppendStepSummary(report.ToString());
        Assert.NotEmpty(results);
    }

    private static IEnumerable<Case> BuildCases()
    {
        const string good = "bazaar-wire/1";
        Case Baseline(string group, string? alpn = null) => new(group, alpn ?? good, alpn ?? good, Bind.Ipv4Loopback, Remote.IpEndPointMatching, CertRecipe.Pkcs12UserKeySet, true, true, "localhost");

        // A: ALPN spelling alone, everything else as the Bazaar modules use it today (UserKeySet cert, ALPN on both option objects).
        foreach (var alpn in _alpns) yield return Baseline("A-alpn", alpn);

        // A2: ALPN mismatch on purpose, so the exact message of a real mismatch is on record to compare with the failing tests.
        yield return Baseline("A2-alpn-mismatch") with { ClientAlpn = "bazaar-wire/1", ServerAlpn = "h3" };

        // B: bind address x client endpoint kind (IPv4/IPv6 mix-ups), for the slash ALPN and for h3.
        foreach (var alpn in new[] { good, "h3" })
        {
            foreach (var bind in Enum.GetValues<Bind>())
            {
                foreach (var remote in Enum.GetValues<Remote>()) yield return Baseline("B-bind-remote", alpn) with { Bind = bind, Remote = remote };
            }
        }

        // C: certificate recipe x ALPN placement x callback style (Schannel key persistence, option duplication).
        foreach (var alpn in new[] { good, "h3" })
        {
            foreach (var cert in Enum.GetValues<CertRecipe>())
            {
                foreach (var serverAuthAlpn in new[] { true, false })
                {
                    foreach (var asyncCallback in new[] { true, false }) yield return Baseline("C-cert-alpn-callback", alpn) with { Cert = cert, ServerAuthAlpn = serverAuthAlpn, AsyncCallback = asyncCallback };
                }
            }
        }

        // D: client TargetHost against a certificate for localhost.
        foreach (var alpn in new[] { good, "h3" })
        {
            foreach (var host in new string?[] { null, "localhost", "127.0.0.1", "::1", "wrong.example" }) yield return Baseline("D-targethost", alpn) with { TargetHost = host };
        }

        // E: the Bazaar combination as the modules build it: Dns localhost endpoint, IPv4 loopback bind, TargetHost localhost.
        yield return Baseline("E-bazaar-shape") with { Remote = Remote.DnsLocalhost };
    }

    private static async Task<Result> RunAsync(Case c, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var serverText = "not reached";
        var clientText = "not reached";
        var negotiated = "-";
        X509Certificate2? cert = null;
        QuicListener? listener = null;
        try
        {
            cert = CreateCertificate(c.Cert);
            var serverAlpn = new SslApplicationProtocol(c.ServerAlpn);
            var listenAddress = c.Bind switch
            {
                Bind.Ipv4Loopback => IPAddress.Loopback,
                Bind.Ipv6Loopback => IPAddress.IPv6Loopback,
                _ => IPAddress.Any,
            };
            var serverNotes = new List<string>();
            ValueTask<QuicServerConnectionOptions> Options(QuicConnection _, SslClientHelloInfo hello, CancellationToken __)
            {
                serverNotes.Add($"hello sni='{hello.ServerName}'");
                var auth = new SslServerAuthenticationOptions { ServerCertificate = cert, ClientCertificateRequired = false };
                if (c.ServerAuthAlpn) auth.ApplicationProtocols = [serverAlpn];
                return ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    ServerAuthenticationOptions = auth,
                });
            }

            Func<QuicConnection, SslClientHelloInfo, CancellationToken, ValueTask<QuicServerConnectionOptions>> callback = c.AsyncCallback
                ? async (connection, hello, token) =>
                {
                    await Task.Yield();
                    return await Options(connection, hello, token);
                }
            : (connection, hello, token) => Options(connection, hello, token);

            listener = await QuicListener.ListenAsync(
                new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(listenAddress, 0),
                    ApplicationProtocols = [serverAlpn],
                    ConnectionOptionsCallback = callback,
                },
                cancellationToken);
            var port = listener.LocalEndPoint.Port;
            EndPoint remote = c.Remote switch
            {
                Remote.DnsLocalhost => new DnsEndPoint("localhost", port),
                Remote.Dns127001 => new DnsEndPoint("127.0.0.1", port),
                _ => new IPEndPoint(c.Bind == Bind.Ipv6Loopback ? IPAddress.IPv6Loopback : IPAddress.Loopback, port),
            };

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_caseTimeout);
            var accept = Task.Run(async () =>
            {
                try
                {
                    await using var accepted = await listener.AcceptConnectionAsync(limit.Token);
                    return $"accepted peer={accepted.RemoteEndPoint} negotiated={accepted.NegotiatedApplicationProtocol} {string.Join("; ", serverNotes)}";
                }
                catch (Exception ex)
                {
                    return $"accept failed: {Chain(ex)} {string.Join("; ", serverNotes)}";
                }
            });

            var policyErrors = "(callback not reached)";
            var clientAuth = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [new SslApplicationProtocol(c.ClientAlpn)],
#pragma warning disable CA5359 // Diagnostic loopback run: accept any certificate so only ALPN/endpoint/key problems show, and record the policy errors.
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                {
                    policyErrors = errors.ToString();
                    return true;
                },
#pragma warning restore CA5359
            };
            if (c.TargetHost is not null) clientAuth.TargetHost = c.TargetHost;
            try
            {
                await using var client = await QuicConnection.ConnectAsync(
                    new QuicClientConnectionOptions { RemoteEndPoint = remote, DefaultStreamErrorCode = 0, DefaultCloseErrorCode = 0, ClientAuthenticationOptions = clientAuth },
                    limit.Token);
                negotiated = client.NegotiatedApplicationProtocol.ToString();
                clientText = $"connected remote={client.RemoteEndPoint} policyErrors={policyErrors}";
                await client.CloseAsync(0, CancellationToken.None);
            }
            catch (Exception ex)
            {
                clientText = $"{Chain(ex)} policyErrors={policyErrors}";
            }

            serverText = await accept.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false) is { } text ? text : "(no result)";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            clientText = $"SETUP {Chain(ex)}";
        }
        finally
        {
            if (listener is not null) await listener.DisposeAsync();
            cert?.Dispose();
        }

        stopwatch.Stop();
        return new Result(c, clientText.StartsWith("connected", StringComparison.Ordinal) && serverText.StartsWith("accepted", StringComparison.Ordinal), clientText, serverText, negotiated, stopwatch.ElapsedMilliseconds);
    }

    private static async Task<string> RunBazaarAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        try
        {
            using var cert = TestCertificates.CreateLocalhost();
            await using var listener = new QuicWireListener(new QuicWireListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                Certificate = cert,
                Codecs = ["json"],
                Diagnostics = text => { lock (notes) notes.Add("listener: " + text); },
            });
            await listener.StartAsync(cancellationToken);
            var offer = listener.Offers[0] with { Endpoint = $"localhost:{listener.LocalEndPoint!.Port}" };
            var connector = new QuicWireConnector(new QuicWireConnectorOptions
            {
                PinCertificate = QuicCertificatePin.Sha256(cert.GetCertHash(HashAlgorithmName.SHA256)),
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });
            try
            {
                await using var client = await connector.ConnectAsync(offer, ["json"], cancellationToken);
                notes.Add($"client connected, profile={client.Profile}");
            }
            catch (Exception ex)
            {
                notes.Add($"client FAILED: {ex.GetType().Name}: {ex.Message}");
                if (ex.Data[QuicWireSupport.DiagnosticsDataKey] is string data) notes.Add("client diagnostics data: " + data);
            }

            await Task.Delay(300, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notes.Add($"SETUP {Chain(ex)}");
        }

        lock (notes) return string.Join(Environment.NewLine + "    ", notes);
    }

    private static X509Certificate2 CreateCertificate(CertRecipe recipe)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        if (recipe != CertRecipe.Pkcs12UserNoSan)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            if (recipe == CertRecipe.Pkcs12UserFullSan) names.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
        }

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        if (recipe == CertRecipe.EphemeralDirect) return created;
        using (created)
        {
            var flags = recipe switch
            {
                CertRecipe.Pkcs12Ephemeral => X509KeyStorageFlags.EphemeralKeySet,
                CertRecipe.Pkcs12UserPersist => X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet,
                _ => X509KeyStorageFlags.UserKeySet,
            };
            return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx, string.Empty), string.Empty, flags);
        }
    }

    private static void PrintEnvironment(Action<string> emit)
    {
        emit("=== QUIC handshake diagnostic matrix (never fails the job; read the rows) ===");
        emit($"OS: {RuntimeInformation.OSDescription} | arch {RuntimeInformation.OSArchitecture} | runtime {RuntimeInformation.FrameworkDescription}");
        emit($"QuicConnection.IsSupported={QuicConnection.IsSupported} QuicListener.IsSupported={QuicListener.IsSupported} Socket.OSSupportsIPv4={Socket.OSSupportsIPv4} OSSupportsIPv6={Socket.OSSupportsIPv6}");
        emit($"Dns localhost -> {SafeResolve("localhost")}");
        emit($"Dns 127.0.0.1 -> {SafeResolve("127.0.0.1")}");
        emit($"user={Environment.UserName} machine={Environment.MachineName} interactive={Environment.UserInteractive}");
        using var cert = CreateCertificate(CertRecipe.Pkcs12UserKeySet);
        emit($"sample cert: subject={cert.Subject} hasPrivateKey={cert.HasPrivateKey} notBefore={cert.NotBefore:O} sha256={Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256))[..16]}");
        emit($"cases per run: {BuildCases().Count()}, per-case timeout {_caseTimeout.TotalSeconds}s");
    }

    private static string SafeResolve(string name)
    {
        try
        {
            return string.Join(", ", Dns.GetHostAddresses(name).Select(static a => a.ToString()));
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void Pivot(List<Result> results, string title, Func<Result, string> key, Action<string> emit)
    {
        emit($"{title}: " + string.Join(" | ", results.GroupBy(key).Select(g => $"{g.Key} {g.Count(static r => r.Ok)}/{g.Count()}")));
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')[0].Trim();
        // Strip the volatile parts (ports, addresses) so the same failure groups together.
        return System.Text.RegularExpressions.Regex.Replace(line, @"policyErrors=\S+|\b\d{1,3}(\.\d{1,3}){3}(:\d+)?\b|\[::1\]:\d+", string.Empty, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private static string Chain(Exception exception)
    {
        var text = new StringBuilder();
        var depth = 0;
        for (var current = exception; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            if (depth > 0) text.Append(" -> ");
            text.Append(current.GetType().Name);
            if (current is QuicException quic) text.Append(CultureInfo.InvariantCulture, $"(QuicError={quic.QuicError}, Transport={quic.TransportErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}, App={quic.ApplicationErrorCode?.ToString(CultureInfo.InvariantCulture) ?? "none"})");
            text.Append(CultureInfo.InvariantCulture, $"(0x{current.HResult:X8}): ").Append(current.Message.ReplaceLineEndings(" "));
        }

        return text.ToString();
    }

    private static void TryAppendStepSummary(string text)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } path)
            {
                File.AppendAllText(path, "### QUIC handshake diagnostic matrix" + Environment.NewLine + "```text" + Environment.NewLine + text + "```" + Environment.NewLine);
            }
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }
}
