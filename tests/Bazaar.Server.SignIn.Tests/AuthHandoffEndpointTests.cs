using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.SignIn.Tests;

/// <summary>The native redeem route: <c>POST /auth/handoff</c> (doc 32 section 4).</summary>
public sealed class AuthHandoffEndpointTests
{
    private static readonly SignInIdentity _who = new(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid());
    private static readonly DateTimeOffset _start = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeTokenService : ITokenService
    {
        public TokenIssueFailure? Refuse { get; init; }

        public bool Unavailable { get; init; }

        public List<(Guid DeviceId, Guid HumanId, DeviceInfo Device)> Issued { get; } = [];

        public Task<TokenPair> IssueAsync(Guid deviceId, Guid humanId, DeviceInfo device, CancellationToken ct = default)
        {
            if (Unavailable) throw new AccessTokenIssuerUnavailableException();
            if (Refuse is { } reason) throw new TokenIssueException(reason);
            Issued.Add((deviceId, humanId, device));
            return Task.FromResult(new TokenPair("access-1", _start.AddMinutes(15), "refresh-1", _start.AddDays(30)));
        }

        public Task<TokenPair> RefreshAsync(string refresh, DeviceInfo device, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RevokeDeviceAsync(Guid deviceId, PrincipalRef by, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));

        public void Dispose()
        {
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public required HttpClient Client { get; init; }

        public required FakeClock Time { get; init; }

        public required CapturingLogger Log { get; init; }

        public IHandoffCodeStore Codes => App.Services.GetRequiredService<IHandoffCodeStore>();

        public Task<string> CodeAsync(string audience = SignInAudiences.Native) => Codes.IssueAsync(_who, audience, Time.GetUtcNow(), Ct);

        public Task<HttpResponseMessage> PostAsync(string json) =>
            Client.PostAsync("/auth/handoff", new StringContent(json, Encoding.UTF8, "application/json"), Ct);

        public Task<HttpResponseMessage> RedeemAsync(string code) =>
            PostAsync(JsonSerializer.Serialize(new { code, device_class = "windows", client_build = "0.1.0+abc" }));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(Ct);
            await App.DisposeAsync();
        }
    }

    private static async Task<Host> StartAsync(
        FakeTokenService? tokens,
        Dictionary<string, string?>? settings = null,
        IPAddress? remote = null)
    {
        var time = new FakeClock(_start);
        var log = new CapturingLogger();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(log);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new() { ["signin:enabled"] = "true" }).Build();
        new SignInComponent().AddServices(builder.Services, config);
        builder.Services.AddSingleton<TimeProvider>(time);
        if (tokens is not null) builder.Services.AddSingleton<ITokenService>(tokens);
        var app = builder.Build();
        if (remote is not null)
        {
            app.Use((ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = remote;
                return next();
            });
        }

        app.MapBazaarSignIn();
        await app.StartAsync(Ct);
        var uri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        return new Host { App = app, Client = new HttpClient { BaseAddress = uri }, Time = time, Log = log };
    }

    [Fact]
    public async Task Redeem_returns_the_pair_for_the_device_in_the_codes_identity()
    {
        var tokens = new FakeTokenService();
        await using var host = await StartAsync(tokens);
        var code = await host.CodeAsync();

        var res = await host.RedeemAsync(code);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonElement.Parse(await res.Content.ReadAsStringAsync(Ct));
        Assert.Equal("access-1", json.GetProperty("access_token").GetString());
        Assert.Equal("Bearer", json.GetProperty("token_type").GetString());
        Assert.Equal(900, json.GetProperty("expires_in").GetInt32());
        Assert.Equal("refresh-1", json.GetProperty("refresh_token").GetString());
        var issued = Assert.Single(tokens.Issued);
        Assert.Equal(_who.DeviceId, issued.DeviceId);
        Assert.Equal(_who.PrincipalId, issued.HumanId);
        Assert.Equal("windows", issued.Device.DeviceClass);
        Assert.Equal("0.1.0+abc", issued.Device.ClientBuild);
    }

    [Fact]
    public async Task A_code_redeems_once()
    {
        await using var host = await StartAsync(new FakeTokenService());
        var code = await host.CodeAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.RedeemAsync(code)).StatusCode);
        var second = await host.RedeemAsync(code);

        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Contains("invalid_grant", await second.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_admin_code_fails_and_is_burned()
    {
        var tokens = new FakeTokenService();
        await using var host = await StartAsync(tokens);
        var code = await host.CodeAsync(SignInAudiences.Admin);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RedeemAsync(code)).StatusCode);

        // Burned: the admin audience can no longer redeem it either.
        Assert.Null(await host.Codes.RedeemAsync(code, SignInAudiences.Admin, host.Time.GetUtcNow(), Ct));
        Assert.Empty(tokens.Issued);
    }

    [Fact]
    public async Task An_expired_code_fails()
    {
        await using var host = await StartAsync(new FakeTokenService());
        var code = await host.CodeAsync();
        host.Time.Advance(TimeSpan.FromSeconds(61));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.RedeemAsync(code)).StatusCode);
    }

    [Fact]
    public async Task Unknown_and_used_codes_answer_byte_identical_bodies_and_the_log_never_holds_the_code()
    {
        await using var host = await StartAsync(new FakeTokenService());
        var code = await host.CodeAsync();
        await host.RedeemAsync(code);

        var used = await host.RedeemAsync(code);
        var unknown = await host.RedeemAsync("no-such-code");
        var oversized = await host.RedeemAsync(new string('x', 400));

        var usedBytes = await used.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, used.StatusCode);
        Assert.Equal(usedBytes, await unknown.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(usedBytes, await oversized.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(used.Content.Headers.ContentType?.ToString(), unknown.Content.Headers.ContentType?.ToString());
        Assert.NotEmpty(host.Log.Lines);
        Assert.DoesNotContain(host.Log.Lines, l => l.Contains(code, StringComparison.Ordinal) || l.Contains("no-such-code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_code_over_the_length_cap_answers_the_same_401_without_reaching_the_store()
    {
        await using var host = await StartAsync(new FakeTokenService());

        var res = await host.RedeemAsync(new string('x', 600));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("invalid_grant", await res.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"code\":\"abc\"}")]
    [InlineData("{\"device_class\":\"windows\"}")]
    [InlineData("{\"code\":\" \",\"device_class\":\"windows\"}")]
    public async Task Missing_or_malformed_fields_answer_400(string body)
    {
        await using var host = await StartAsync(new FakeTokenService());

        var res = await host.PostAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("malformed_request", await res.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_token_service_answers_503_and_keeps_the_code()
    {
        await using var host = await StartAsync(tokens: null);
        var code = await host.CodeAsync();

        var res = await host.RedeemAsync(code);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("unavailable", await res.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.NotNull(await host.Codes.RedeemAsync(code, SignInAudiences.Native, host.Time.GetUtcNow(), Ct));
    }

    [Fact]
    public async Task An_unavailable_issuer_answers_503()
    {
        await using var host = await StartAsync(new FakeTokenService { Unavailable = true });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.RedeemAsync(await host.CodeAsync())).StatusCode);
    }

    [Theory]
    [InlineData(TokenIssueFailure.DeviceAlreadyExists)]
    [InlineData(TokenIssueFailure.InvalidDevice)]
    public async Task A_refused_device_answers_the_same_generic_401(TokenIssueFailure reason)
    {
        await using var host = await StartAsync(new FakeTokenService { Refuse = reason });

        var res = await host.RedeemAsync(await host.CodeAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("invalid_grant", await res.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plaintext_from_off_machine_is_refused_with_403()
    {
        var tokens = new FakeTokenService();
        await using var host = await StartAsync(tokens, remote: IPAddress.Parse("203.0.113.9"));
        var code = await host.CodeAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await host.RedeemAsync(code)).StatusCode);
        Assert.Empty(tokens.Issued);
        Assert.NotNull(await host.Codes.RedeemAsync(code, SignInAudiences.Native, host.Time.GetUtcNow(), Ct));
    }

    [Fact]
    public async Task The_overlay_setting_allows_off_machine_plaintext()
    {
        await using var host = await StartAsync(
            new FakeTokenService(),
            new() { ["signin:enabled"] = "true", ["Bazaar:Wire:AllowPlaintextOverlay"] = "true" },
            IPAddress.Parse("203.0.113.9"));

        Assert.Equal(HttpStatusCode.OK, (await host.RedeemAsync(await host.CodeAsync())).StatusCode);
    }

    [Fact]
    public async Task The_route_is_off_unless_native_sign_in_is_enabled_and_takes_post_only()
    {
        await using var off = await StartAsync(new FakeTokenService(), new() { ["signin:adminEnabled"] = "true" });
        Assert.Equal(HttpStatusCode.NotFound, (await off.RedeemAsync("x")).StatusCode);

        await using var on = await StartAsync(new FakeTokenService());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await on.Client.GetAsync("/auth/handoff", Ct)).StatusCode);
    }
}
