// Spike S16: does SocketsHttpHandler on .NET 10 route requests through a local
// SOCKS4/4a/5 proxy (Proxy = new WebProxy("socks5://127.0.0.1:PORT")), and do
// https:// proxy URIs work?
//
// Everything runs in-process: a tiny HTTP target, an in-process proxy that counts
// what it sees, and an HttpClient whose handler uses the proxy under test.
// Each variant prints one JSON line on stdout; a human summary goes to stderr.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

const int TimedRequests = 30; // after one untimed warm-up request per variant

// ---------- target: tiny HTTP/1.1 server, Connection: close ----------
var targetListener = new TcpListener(IPAddress.Loopback, 0);
targetListener.Start();
int targetPort = ((IPEndPoint)targetListener.LocalEndpoint).Port;
_ = Task.Run(async () =>
{
    while (true)
    {
        TcpClient c;
        try { c = await targetListener.AcceptTcpClientAsync(); }
        catch { return; }
        _ = ServeTargetAsync(c);
    }
});

var cert = MakeCert();
var variants = new List<Variant>
{
    new("socks5-ipv4", ProxyKind.Socks, "socks5", "127.0.0.1", false, false, false, false, true),
    new("socks5-domain", ProxyKind.Socks, "socks5", "localhost", false, false, false, false, true),
    new("socks5h-ipv4", ProxyKind.Socks, "socks5h", "127.0.0.1", false, false, false, false, true),
    new("socks5h-domain", ProxyKind.Socks, "socks5h", "localhost", false, false, false, false, true),
    new("socks4-ipv4", ProxyKind.Socks, "socks4", "127.0.0.1", false, false, false, false, true),
    new("socks4a-domain", ProxyKind.Socks, "socks4a", "localhost", false, false, false, false, true),
    new("socks5-userpass-creds", ProxyKind.Socks, "socks5", "127.0.0.1", true, true, false, false, true),
    new("socks5-userpass-nocreds (control)", ProxyKind.Socks, "socks5", "127.0.0.1", true, false, false, false, false),
    new("socks5-proxy-stopped (control)", ProxyKind.Socks, "socks5", "127.0.0.1", false, false, false, true, false),
    new("http-absolute-form (baseline)", ProxyKind.Http, "http", "127.0.0.1", false, false, false, false, true),
    new("https-proxy-uri, cert not trusted (validation bypassed)", ProxyKind.HttpTls, "https", "127.0.0.1", false, false, true, false, true),
    new("https-proxy-uri, cert not trusted (default validation)", ProxyKind.HttpTls, "https", "127.0.0.1", false, false, false, false, true),
};

var only = args.Length > 0 ? args[0] : null;
foreach (var v in variants)
{
    if (only != null && !v.Name.StartsWith(only, StringComparison.OrdinalIgnoreCase)) continue;
    var r = await RunVariantAsync(v, targetPort, cert);
    Console.WriteLine(JsonSerializer.Serialize(r));
    Console.Out.Flush();
    Console.Error.WriteLine($"{r.Variant,-62} outcome={r.Outcome,-4} ok={r.Succeeded}/{r.Attempts} connectSeen={r.ProxyConnectSeen} forwards={r.ProxyForwards} median={r.MedianMs?.ToString("F2") ?? "-"}ms {(r.Error != null ? "err=" + r.Error : "")}");
}

targetListener.Stop();
return 0;

// ---------- variant runner ----------
static async Task<Result> RunVariantAsync(Variant v, int targetPort, X509Certificate2 cert)
{
    var proxy = new Proxy(v.Kind, v.ProxyAuth ? "u1" : null, v.ProxyAuth ? "p1" : null, cert);
    proxy.Start();
    if (v.ProxyStopped) proxy.Stop();

    string proxyUri = $"{v.Scheme}://127.0.0.1:{proxy.Port}";
    var wp = new WebProxy(proxyUri) { BypassProxyOnLocal = false };
    if (v.SendCreds) wp.Credentials = new NetworkCredential("u1", "p1");

    var handler = new SocketsHttpHandler
    {
        Proxy = wp,
        UseProxy = true,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };
    if (v.AcceptAnyCert)
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

    var url = $"http://{v.TargetHost}:{targetPort}/s16";
    var lat = new List<double>();
    string error = null;
    int attempts = 0, ok = 0;

    using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
    {
        for (int i = 0; i < TimedRequests + 1; i++)
        {
            attempts++;
            var sw = Stopwatch.StartNew();
            try
            {
                var body = await http.GetStringAsync(url);
                sw.Stop();
                if (body != "ok-from-target") throw new InvalidOperationException("unexpected body: " + body);
                ok++;
                if (i > 0) lat.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception e)
            {
                sw.Stop();
                error = Chain(e);
                break; // first failure ends the variant; latency is only reported for all-pass runs
            }
        }
    }

    proxy.Stop();
    bool pass = error == null && ok == attempts && attempts == TimedRequests + 1;
    var r = new Result
    {
        Variant = v.Name,
        ProxyUri = proxyUri,
        Target = url,
        Expected = v.ExpectPass ? "pass" : "fail",
        Outcome = pass ? "pass" : "fail",
        Attempts = attempts,
        Succeeded = ok,
        ProxyConnectSeen = proxy.ConnectSeen,
        ProxyForwards = proxy.Forwards,
        ProxyConnected = proxy.Connected,
        Error = error,
        ProxyLog = proxy.Log.ToArray().Take(4).ToArray(),
    };
    if (pass && lat.Count > 0)
    {
        var s = lat.OrderBy(x => x).ToArray();
        r.MedianMs = Median(s);
        r.MinMs = s[0];
        r.MaxMs = s[^1];
        r.P95Ms = s[(int)Math.Ceiling(0.95 * s.Length) - 1];
    }
    return r;
}

static double Median(double[] sorted)
{
    int n = sorted.Length;
    return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
}

static string Chain(Exception e)
{
    var parts = new List<string>();
    for (var x = e; x != null; x = x.InnerException) parts.Add($"{x.GetType().Name}: {x.Message}");
    return string.Join(" -> ", parts);
}

static async Task ServeTargetAsync(TcpClient c)
{
    using (c)
    {
        var s = c.GetStream();
        await Io.ReadHeadAsync(s);
        var body = Encoding.ASCII.GetBytes("ok-from-target");
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }
}

static X509Certificate2 MakeCert()
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var san = new SubjectAlternativeNameBuilder();
    san.AddIpAddress(IPAddress.Loopback);
    san.AddDnsName("localhost");
    req.CertificateExtensions.Add(san.Build());
    var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
    return new X509Certificate2(self.Export(X509ContentType.Pfx, "pw"), "pw", X509KeyStorageFlags.Exportable);
}

// ---------- types ----------
public enum ProxyKind { Socks, Http, HttpTls }

public sealed record Variant(
    string Name, ProxyKind Kind, string Scheme, string TargetHost,
    bool ProxyAuth, bool SendCreds, bool AcceptAnyCert, bool ProxyStopped, bool ExpectPass);

public sealed class Result
{
    public string Variant { get; set; } = "";
    public string ProxyUri { get; set; } = "";
    public string Target { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Outcome { get; set; } = "";
    public int Attempts { get; set; }
    public int Succeeded { get; set; }
    public int ProxyConnectSeen { get; set; }
    public int ProxyForwards { get; set; }
    public int ProxyConnected { get; set; }
    public double? MedianMs { get; set; }
    public double? MinMs { get; set; }
    public double? MaxMs { get; set; }
    public double? P95Ms { get; set; }
    public string Error { get; set; }
    public string[] ProxyLog { get; set; } = Array.Empty<string>();
}

// ---------- in-process proxy ----------
public sealed class Proxy
{
    readonly ProxyKind _kind;
    readonly string _user, _pass;
    readonly X509Certificate2 _cert;
    TcpListener _listener;

    public int Port { get; private set; }
    public int ConnectSeen;   // SOCKS CONNECT requests or HTTP CONNECT requests received
    public int Forwards;      // absolute-form HTTP requests forwarded (plain http target via http proxy)
    public int Connected;     // tunnels actually established to the target
    public readonly ConcurrentQueue<string> Log = new ConcurrentQueue<string>();

    public Proxy(ProxyKind kind, string user, string pass, X509Certificate2 cert)
    {
        _kind = kind; _user = user; _pass = pass; _cert = cert;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
    }

    async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = HandleAsync(c);
        }
    }

    async Task HandleAsync(TcpClient c)
    {
        try
        {
            using (c)
            {
                Stream s = c.GetStream();
                if (_kind == ProxyKind.HttpTls)
                {
                    var ssl = new SslStream(s, false);
                    await ssl.AuthenticateAsServerAsync(_cert, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    s = ssl;
                }
                if (_kind == ProxyKind.Socks) await SocksAsync(s);
                else await HttpAsync(s);
            }
        }
        catch (Exception e)
        {
            Log.Enqueue($"proxy-conn-error: {e.GetType().Name}: {e.Message}");
        }
    }

    // ----- SOCKS -----
    async Task SocksAsync(Stream s)
    {
        byte v = (await Io.ReadExactAsync(s, 1))[0];
        if (v == 5) await Socks5Async(s);
        else if (v == 4) await Socks4Async(s);
        else Log.Enqueue($"bad-socks-version {v}");
    }

    async Task Socks5Async(Stream s)
    {
        int nm = (await Io.ReadExactAsync(s, 1))[0];
        byte[] methods = await Io.ReadExactAsync(s, nm);
        if (_user != null)
        {
            if (!methods.Contains((byte)2))
            {
                await s.WriteAsync(new byte[] { 5, 0xFF });
                Log.Enqueue("socks5: no acceptable method (wanted user/pass)");
                return;
            }
            await s.WriteAsync(new byte[] { 5, 2 });
            await Io.ReadExactAsync(s, 1); // auth subnegotiation version
            int ul = (await Io.ReadExactAsync(s, 1))[0];
            string u = Encoding.UTF8.GetString(await Io.ReadExactAsync(s, ul));
            int pl = (await Io.ReadExactAsync(s, 1))[0];
            string p = Encoding.UTF8.GetString(await Io.ReadExactAsync(s, pl));
            bool okAuth = u == _user && p == _pass;
            await s.WriteAsync(new byte[] { 1, (byte)(okAuth ? 0 : 1) });
            Log.Enqueue(okAuth ? "socks5: auth ok" : $"socks5: auth FAILED for user '{u}'");
            if (!okAuth) return;
        }
        else
        {
            if (!methods.Contains((byte)0))
            {
                await s.WriteAsync(new byte[] { 5, 0xFF });
                Log.Enqueue("socks5: client offered no no-auth method");
                return;
            }
            await s.WriteAsync(new byte[] { 5, 0 });
        }

        byte[] h = await Io.ReadExactAsync(s, 4); // VER CMD RSV ATYP
        if (h[1] != 1)
        {
            await s.WriteAsync(new byte[] { 5, 7, 0, 1, 0, 0, 0, 0, 0, 0 });
            return;
        }
        string host;
        IPAddress[] addrs;
        switch (h[3])
        {
            case 1: // IPv4
            {
                var b = await Io.ReadExactAsync(s, 4);
                var a = new IPAddress(b);
                host = a.ToString();
                addrs = new[] { a };
                break;
            }
            case 4: // IPv6
            {
                var b = await Io.ReadExactAsync(s, 16);
                var a = new IPAddress(b);
                host = a.ToString();
                addrs = new[] { a };
                break;
            }
            case 3: // domain name (proxy-side resolution)
            {
                int dl = (await Io.ReadExactAsync(s, 1))[0];
                host = Encoding.ASCII.GetString(await Io.ReadExactAsync(s, dl));
                addrs = await ResolveAsync(host);
                break;
            }
            default:
                await s.WriteAsync(new byte[] { 5, 8, 0, 1, 0, 0, 0, 0, 0, 0 });
                return;
        }
        byte[] pb = await Io.ReadExactAsync(s, 2);
        int port = (pb[0] << 8) | pb[1];

        ConnectSeen++;
        Log.Enqueue($"CONNECT socks5 atyp={h[3]} {host}:{port}");
        var t = await TryConnectAsync(addrs, port);
        if (t == null)
        {
            await s.WriteAsync(new byte[] { 5, 4, 0, 1, 0, 0, 0, 0, 0, 0 });
            return;
        }
        await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        Connected++;
        using (t) await Io.RelayAsync(s, t.GetStream());
    }

    async Task Socks4Async(Stream s)
    {
        int cd = (await Io.ReadExactAsync(s, 1))[0];
        byte[] pb = await Io.ReadExactAsync(s, 2);
        byte[] ip = await Io.ReadExactAsync(s, 4);
        await Io.ReadNulTerminatedAsync(s); // userid
        string host;
        IPAddress[] addrs;
        if (ip[0] == 0 && ip[1] == 0 && ip[2] == 0 && ip[3] != 0) // SOCKS4a: domain follows
        {
            host = await Io.ReadNulTerminatedAsync(s);
            addrs = await ResolveAsync(host);
        }
        else
        {
            var a = new IPAddress(ip);
            host = a.ToString();
            addrs = new[] { a };
        }
        int port = (pb[0] << 8) | pb[1];
        if (cd != 1)
        {
            await s.WriteAsync(new byte[] { 0, 0x5B, pb[0], pb[1], 0, 0, 0, 0 });
            return;
        }
        ConnectSeen++;
        Log.Enqueue($"CONNECT socks4 ip={(ip[0] == 0 && ip[1] == 0 && ip[2] == 0 ? "4a" : "4")} {host}:{port}");
        var t = await TryConnectAsync(addrs, port);
        if (t == null)
        {
            await s.WriteAsync(new byte[] { 0, 0x5B, pb[0], pb[1], 0, 0, 0, 0 });
            return;
        }
        await s.WriteAsync(new byte[] { 0, 0x5A, pb[0], pb[1], ip[0], ip[1], ip[2], ip[3] });
        Connected++;
        using (t) await Io.RelayAsync(s, t.GetStream());
    }

    // ----- HTTP proxy (absolute-form forwarding + CONNECT) -----
    async Task HttpAsync(Stream s)
    {
        byte[] head = await Io.ReadHeadAsync(s);
        string text = Encoding.ASCII.GetString(head);
        string[] lines = text.Split("\r\n");
        string[] rl = lines[0].Split(' ');
        if (rl[0] == "CONNECT")
        {
            ConnectSeen++;
            string[] hp = rl[1].Split(':');
            string host = hp[0];
            int port = int.Parse(hp[1]);
            Log.Enqueue($"CONNECT http {host}:{port}");
            var t = await TryConnectAsync(await ResolveAsync(host), port);
            if (t == null)
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"));
                return;
            }
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"));
            Connected++;
            using (t) await Io.RelayAsync(s, t.GetStream());
            return;
        }

        Forwards++;
        var uri = new Uri(rl[1]);
        Log.Enqueue($"FORWARD {rl[0]} {uri.Host}:{uri.Port}{uri.PathAndQuery}");
        var tg = await TryConnectAsync(await ResolveAsync(uri.Host), uri.Port);
        if (tg == null)
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n"));
            return;
        }
        Connected++;
        var sb = new StringBuilder();
        sb.Append($"{rl[0]} {uri.PathAndQuery} {rl[2]}\r\n");
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) break;
            if (lines[i].StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(lines[i]).Append("\r\n");
        }
        sb.Append("\r\n");
        using (tg)
        {
            var tgs = tg.GetStream();
            await tgs.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
            await Io.RelayAsync(s, tgs);
        }
    }

    static async Task<IPAddress[]> ResolveAsync(string host)
    {
        try { return await Dns.GetHostAddressesAsync(host); }
        catch { return Array.Empty<IPAddress>(); }
    }

    static async Task<TcpClient> TryConnectAsync(IPAddress[] addrs, int port)
    {
        foreach (var a in addrs.OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var c = new TcpClient(a.AddressFamily);
            try
            {
                await c.ConnectAsync(a, port);
                return c;
            }
            catch { c.Dispose(); }
        }
        return null;
    }
}

public static class Io
{
    public static async Task<byte[]> ReadExactAsync(Stream s, int n)
    {
        var b = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = await s.ReadAsync(b.AsMemory(off, n - off));
            if (r == 0) throw new EndOfStreamException("eof");
            off += r;
        }
        return b;
    }

    public static async Task<string> ReadNulTerminatedAsync(Stream s)
    {
        var ms = new MemoryStream();
        while (true)
        {
            byte b = (await ReadExactAsync(s, 1))[0];
            if (b == 0) break;
            ms.WriteByte(b);
            if (ms.Length > 1024) throw new InvalidDataException("string too long");
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    public static async Task<byte[]> ReadHeadAsync(Stream s)
    {
        var ms = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            int r = await s.ReadAsync(one.AsMemory(0, 1));
            if (r == 0) throw new EndOfStreamException("eof in head");
            ms.WriteByte(one[0]);
            long len = ms.Length;
            if (len >= 4)
            {
                var buf = ms.GetBuffer();
                if (buf[len - 4] == 13 && buf[len - 3] == 10 && buf[len - 2] == 13 && buf[len - 1] == 10) break;
            }
            if (len > 65536) throw new InvalidDataException("head too long");
        }
        return ms.ToArray();
    }

    public static async Task RelayAsync(Stream a, Stream b)
    {
        var t1 = Pump(a, b);
        var t2 = Pump(b, a);
        await Task.WhenAny(t1, t2);
        await Task.WhenAny(Task.WhenAll(t1, t2), Task.Delay(5000));
    }

    static async Task Pump(Stream src, Stream dst)
    {
        try { await src.CopyToAsync(dst); } catch { }
        try { await dst.FlushAsync(); } catch { }
    }
}
