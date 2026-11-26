using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.EndToEnd.Tests.Harness;

/// <summary>
/// A keyless stand-in for the GitHub REST API, on https because the real GitHub adapter refuses any other base address (the token
/// travels in a header). It serves the two calls the draft-PR step makes: search open PRs by head, and create a PR. It keeps every
/// request so a test can assert what the adapter sent. Only the pull request side is faked; there is no git remote behind it
/// (GAPS.md, gap 12).
/// </summary>
public sealed class StubGitHubServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly ConcurrentQueue<StubGitHubRequest> _requests = new();
    private readonly ConcurrentDictionary<int, JsonElement> _pulls = new();
    private int _nextNumber = 100;

    private StubGitHubServer(WebApplication app, X509Certificate2 certificate, int port)
    {
        _app = app;
        _certificate = certificate;
        Port = port;
    }

    public int Port { get; }

    /// <summary>The https base address to give the adapter.</summary>
    public Uri BaseAddress => new($"https://127.0.0.1:{Port}/");

    public IReadOnlyCollection<StubGitHubRequest> Requests => _requests.ToArray();

    /// <summary>A handler for the adapter's <see cref="HttpClient"/> that trusts only this stub's certificate.</summary>
    public HttpMessageHandler CreateTrustingHandler() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && cert.GetCertHashString() == _certificate.GetCertHashString(),
    };

    public static async Task<StubGitHubServer> StartAsync()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=stub-github", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o =>
        {
            o.Protocols = HttpProtocols.Http1;
            o.UseHttps(certificate);
        }));
        var app = builder.Build();
        var holder = new StubGitHubServer[1];

        app.MapGet("/repos/{owner}/{repo}/pulls", (HttpContext http, string owner, string repo) =>
        {
            var self = holder[0];
            self.Record(http, null);
            var head = http.Request.Query["head"].ToString();
            var matches = self._pulls.OrderBy(p => p.Key).Select(p => p.Value)
                .Where(p => head.EndsWith(":" + p.GetProperty("head").GetProperty("ref").GetString(), StringComparison.Ordinal))
                .ToList();
            return Results.Text(JsonSerializer.Serialize(matches), "application/json");
        });

        app.MapPost("/repos/{owner}/{repo}/pulls", async (HttpContext http, string owner, string repo) =>
        {
            var self = holder[0];
            using var reader = new StreamReader(http.Request.Body);
            var raw = await reader.ReadToEndAsync();
            self.Record(http, raw);
            using var doc = JsonDocument.Parse(raw);
            var number = Interlocked.Increment(ref self._nextNumber);
            var headRef = doc.RootElement.GetProperty("head").GetString();
            var pull = new
            {
                number,
                node_id = "PR_stub_" + number,
                title = doc.RootElement.GetProperty("title").GetString(),
                draft = doc.RootElement.GetProperty("draft").GetBoolean(),
                html_url = $"https://github.com/{owner}/{repo}/pull/{number}",
                head = new { @ref = headRef, sha = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant() },
                @base = new { @ref = doc.RootElement.GetProperty("base").GetString() },
            };
            var json = JsonSerializer.Serialize(pull);
            self._pulls[number] = JsonElement.Parse(json);
            return Results.Text(json, "application/json", statusCode: 201);
        });

        await app.StartAsync();
        var stub = new StubGitHubServer(app, certificate, new Uri(app.Urls.First()).Port);
        holder[0] = stub;
        return stub;
    }

    private void Record(HttpContext http, string? body) =>
        _requests.Enqueue(new StubGitHubRequest(http.Request.Method, http.Request.Path + http.Request.QueryString, http.Request.Headers.Authorization.ToString(), body));

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _certificate.Dispose();
    }
}

/// <summary>One call the adapter made to the stub GitHub.</summary>
public sealed record StubGitHubRequest(string Method, string PathAndQuery, string Authorization, string? Body);
