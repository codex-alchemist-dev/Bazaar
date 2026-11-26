using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Server.RateLimits;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Setup.Http.Tests.Support;

/// <summary>Who a test request claims to be. Loopback and https are off unless a test says otherwise.</summary>
public sealed record Caller(string? Code = null, string? Token = null, string Remote = "127.0.0.1", bool Https = false);

/// <summary>
/// An in-process host with the provisioning routes mapped on a test server. Every collaborator is a fake the test can read or
/// change. Two request headers set the connection the test server does not know: X-Test-Remote and X-Test-Https.
/// </summary>
public sealed class SetupHarness : IAsyncDisposable
{
    public const string OwnerToken = "owner-token";
    public const string AgentToken = "agent-token";
    public static readonly Guid OwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid AgentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string _remoteHeader = "X-Test-Remote";
    private const string _httpsHeader = "X-Test-Https";

    public InMemoryBootstrapCodeStore Codes { get; } = new();

    public FakeOwnerDirectory Owners { get; } = new();

    public InMemoryApplyResultStore Results { get; } = new();

    public FakeProgressSource Progress { get; } = new();

    public FakeRateLimiter Limiter { get; } = new();

    public FakeAuthenticator Auth { get; } = new();

    public FakeAuditLog Audit { get; } = new();

    public FakeTransportPolicy TransportPolicy { get; } = new();

    public FakeInstanceLock Lock { get; } = new();

    public FakeSettingAudit SettingAudit { get; } = new();

    public FakeSettingCatalog Settings { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero));

    public CapturingLoggerProvider Logs { get; } = new();

    public CountingStep Step { get; } = new("database.schema", 10);

    /// <summary>When null, the verification source is not registered and the status answers a null verification.</summary>
    public SetupVerificationSummary? Verification { get; set; }

    /// <summary>Set to false to leave one dependency unregistered, so the routes answer 503.</summary>
    public bool RegisterGate { get; set; } = true;

    private WebApplication? _app;
    private HttpClient? _client;

    public SetupHarness()
    {
        Auth.Tokens[OwnerToken] = new SetupPrincipal(OwnerId, "owner");
        Auth.Tokens[AgentToken] = new SetupPrincipal(AgentId, "agent");
    }

    /// <summary>The applier the routes use. It runs the counting step, so a test can see every apply.</summary>
    public SetupApplier Applier { get; private set; } = null!;

    public HttpClient Client => _client ?? throw new InvalidOperationException("Start the harness first.");

    /// <summary>Builds and starts the host. Call once per test, after setting the fakes the test needs.</summary>
    public async Task StartAsync()
    {
        Applier = new SetupApplier(Lock, [Step], Audit, SettingAudit, Time, Settings);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILoggerProvider>(Logs);
        builder.Services.AddSingleton<TimeProvider>(Time);
        builder.Services.AddSetupHttp();
        builder.Services.AddSingleton(Applier);
        builder.Services.AddSingleton<IApplyResultStore>(Results);
        builder.Services.AddSingleton<ISetupProgressSource>(Progress);
        builder.Services.AddSingleton<IRateLimiter>(Limiter);
        builder.Services.AddSingleton<IAuditLog>(Audit);
        builder.Services.AddSingleton<ISetupCallerAuthenticator>(Auth);
        builder.Services.AddSingleton<ISetupSettingCatalog>(Settings);
        builder.Services.AddSingleton<ISetupTransportPolicy>(TransportPolicy);
        if (RegisterGate)
        {
            builder.Services.AddSingleton<IBootstrapCodeStore>(Codes);
            builder.Services.AddSingleton<ISetupOwnerDirectory>(Owners);
            builder.Services.AddSingleton<ISetupAccessGate>(new SetupAccessGate(Codes, Owners));
        }

        if (Verification is { } summary)
        {
            builder.Services.AddSingleton<ISetupVerificationSource>(new FakeVerificationSource(summary));
        }

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var remote = context.Request.Headers[_remoteHeader].ToString();
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote.Length == 0 ? "127.0.0.1" : remote);
            if (context.Request.Headers.ContainsKey(_httpsHeader))
            {
                context.Request.Scheme = "https";
            }

            await next(context);
        });
        app.MapSetupEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);

        _app = app;
        _client = app.GetTestClient();
    }

    /// <summary>Sends one request as <paramref name="caller"/>. A null <paramref name="json"/> sends no body.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Caller? caller = null, string? json = null, string contentType = "application/json")
    {
        caller ??= new Caller();
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(_remoteHeader, caller.Remote);
        if (caller.Https)
        {
            request.Headers.Add(_httpsHeader, "1");
        }

        if (caller.Code is not null)
        {
            request.Headers.Add(SetupHttpConstants.BootstrapCodeHeader, caller.Code);
        }

        if (caller.Token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", caller.Token);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, contentType);
        }

        return Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The body of a response as text, read once.</summary>
    public static async Task<string> TextOf(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    /// <summary>The audit rows of one kind, in append order.</summary>
    public IReadOnlyList<AuditEntry> RowsOf(string kind) => Audit.Entries.Where(entry => entry.Kind == kind).ToList();

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync(TestContext.Current.CancellationToken);
            await _app.DisposeAsync();
        }
    }
}

/// <summary>Keeps every formatted log line and exception text, so a test can prove a secret never reached the log.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentBag<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines.ToList();

    public ILogger CreateLogger(string categoryName) => new Capture(_lines);

    public void Dispose()
    {
    }

    private sealed class Capture(ConcurrentBag<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lines.Add(formatter(state, exception) + " " + (exception?.ToString() ?? string.Empty));
        }
    }
}
