using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.SignIn.WebAuthn.Credentials;
using Bazaar.Server.SignIn.WebAuthn.Registration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.SignIn.WebAuthn.Tests;

public sealed class RegistrationEndpointTests
{
    private static readonly Guid _owner = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public bool Fail { get; set; }

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            if (Fail && e.Kind == "signin.passkey.registered")
            {
                throw new InvalidOperationException("down");
            }

            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingSink : IBootstrapCodeSink
    {
        public string? Code { get; private set; }

        public void Announce(string code, Guid principalId) => Code = code;
    }

    /// <summary>Treats a request carrying <c>X-Test-Principal</c> as signed in as that principal.</summary>
    private sealed class HeaderResolver : IRegistrationPrincipalResolver
    {
        public Task<Guid?> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            Task.FromResult(Guid.TryParse(context.Request.Headers["X-Test-Principal"], out var id) ? id : (Guid?)null);
    }

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public required HttpClient Client { get; init; }

        public required string Origin { get; init; }

        public required RecordingAudit Audit { get; init; }

        public required CapturingSink Sink { get; init; }

        public InMemoryPasskeyCredentialStore Store => (InMemoryPasskeyCredentialStore)App.Services.GetRequiredService<IPasskeyCredentialStore>();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(Ct);
            await App.DisposeAsync();
        }

        public Task<HttpResponseMessage> PostAsync(string path, object body, string? origin = null, string? principal = null, string? fakeIp = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Origin", origin ?? Origin);
            if (principal is not null)
            {
                req.Headers.TryAddWithoutValidation("X-Test-Principal", principal);
            }

            if (fakeIp is not null)
            {
                req.Headers.TryAddWithoutValidation("X-Test-Ip", fakeIp);
            }

            return Client.SendAsync(req, Ct);
        }

        public static async Task<JsonElement> ReadAsync(HttpResponseMessage res) => JsonElement.Parse(await res.Content.ReadAsStringAsync(Ct)).Clone();

        public Task<HttpResponseMessage> BeginAsync(string? code, string? label = null, string? principal = null, string? origin = null, string? fakeIp = null) =>
            PostAsync("/signin/register/begin", new { bootstrapCode = code, label }, origin, principal, fakeIp);

        public Task<HttpResponseMessage> FinishAsync(string challengeId, string attestation, string? principal = null) =>
            PostAsync("/signin/register", new { challengeId, attestation }, principal: principal);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static async Task<Host> StartAsync(Dictionary<string, string?>? extra = null, bool withAudit = true, bool signedInResolver = false)
    {
        var port = FreePort();
        var origin = $"http://localhost:{port}";
        var settings = new Dictionary<string, string?>
        {
            ["signin:rpId"] = "localhost",
            ["signin:webauthn:origins"] = origin,
            ["signin:webauthn:bootstrapPrincipalId"] = _owner.ToString(),
        };
        foreach (var kv in extra ?? [])
        {
            settings[kv.Key] = kv.Value;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var audit = new RecordingAudit();
        var sink = new CapturingSink();
        if (withAudit)
        {
            builder.Services.AddSingleton<IAuditLog>(audit);
        }

        builder.Services.AddSingleton<IBootstrapCodeSink>(sink);
        builder.Services.AddSingleton<IPasskeyCredentialStore, InMemoryPasskeyCredentialStore>();
        if (signedInResolver)
        {
            builder.Services.AddSingleton<IRegistrationPrincipalResolver, HeaderResolver>();
        }

        new SignInComponent().AddServices(builder.Services, config);
        new SignInWebAuthnComponent().AddServices(builder.Services, config);
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            if (IPAddress.TryParse(ctx.Request.Headers["X-Test-Ip"], out var ip))
            {
                ctx.Connection.RemoteIpAddress = ip;
            }

            await next();
        });
        app.MapBazaarSignIn();
        app.MapBazaarSignInWebAuthn();
        await app.StartAsync(Ct);
        var uri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = uri };
        return new Host { App = app, Client = client, Origin = origin, Audit = audit, Sink = sink };
    }

    private static string Attest(Host host, TestAuthenticator auth, JsonElement begin) =>
        auth.Attest(begin.GetProperty("challenge").GetString()!, host.Origin, rpId: "localhost");

    private static Task<string> BootstrapCodeAsync(Host host) =>
        Task.FromResult(host.Sink.Code ?? throw new InvalidOperationException("No bootstrap code was announced."));

    [Fact]
    public async Task Bootstrap_code_registers_the_first_passkey_and_it_then_signs_in()
    {
        await using var host = await StartAsync();
        var auth = new TestAuthenticator();
        var code = await BootstrapCodeAsync(host);

        var beginRes = await host.BeginAsync(code, "owner laptop");
        Assert.Equal(HttpStatusCode.OK, beginRes.StatusCode);
        var begin = await Host.ReadAsync(beginRes);
        Assert.Equal("localhost", begin.GetProperty("rp").GetProperty("id").GetString());
        Assert.Equal("none", begin.GetProperty("attestation").GetString());
        Assert.Equal(-7, begin.GetProperty("pubKeyCredParams")[0].GetProperty("alg").GetInt32());
        Assert.Equal("required", begin.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());

        var finish = await host.FinishAsync(begin.GetProperty("id").GetString()!, Attest(host, auth, begin));
        Assert.Equal(HttpStatusCode.OK, finish.StatusCode);

        var stored = await host.Store.FindByCredentialIdAsync(auth.CredentialId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(_owner, stored.PrincipalId);
        Assert.Equal("owner laptop", stored.Label);

        // The registered passkey now signs in through the verifier that replaced the deny-all one.
        var verifier = host.App.Services.GetRequiredService<IPasskeyAssertionVerifier>();
        Assert.IsType<WebAuthnAssertionVerifier>(verifier);
        var challenge = TestAuthenticator.NewChallenge();
        var identity = await verifier.VerifyAsync(challenge, auth.Assert(challenge, 1, host.Origin, "localhost"), Ct);
        Assert.Equal(_owner, identity?.PrincipalId);

        // Audited: the issue (never the code), and the registration with ids only.
        Assert.Contains(host.Audit.Entries, e => e.Kind == "signin.bootstrap.issued" && !e.Payload.Contains(code, StringComparison.Ordinal));
        var registered = Assert.Single(host.Audit.Entries, e => e.Kind == "signin.passkey.registered");
        Assert.Equal(_owner.ToString("D"), registered.Actor);
        Assert.DoesNotContain(TestAuthenticator.B64(auth.CredentialId), registered.Payload, StringComparison.Ordinal);
        Assert.Contains("\"via\":\"bootstrap\"", registered.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bootstrap_code_works_once_and_not_for_a_principal_that_already_has_a_passkey()
    {
        await using var host = await StartAsync();
        var auth = new TestAuthenticator();
        var code = await BootstrapCodeAsync(host);
        var begin = await Host.ReadAsync(await host.BeginAsync(code));
        await host.FinishAsync(begin.GetProperty("id").GetString()!, Attest(host, auth, begin));

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.BeginAsync(code)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_a_missing_code_and_five_failures_all_answer_the_same_401()
    {
        await using var host = await StartAsync();
        var code = await BootstrapCodeAsync(host);

        var wrong = await host.BeginAsync("0000-0000");
        var missing = await host.BeginAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(Ct), await missing.Content.ReadAsStringAsync(Ct));

        for (var i = 0; i < BootstrapCodeService.MaxFailures; i++)
        {
            await host.BeginAsync("bad" + i);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.BeginAsync(code)).StatusCode);
        Assert.Contains(host.Audit.Entries, e => e.Kind == "signin.passkey.register.denied");
    }

    [Fact]
    public async Task Bootstrap_from_a_non_loopback_address_is_refused()
    {
        await using var host = await StartAsync();
        var code = await BootstrapCodeAsync(host);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.BeginAsync(code, fakeIp: "203.0.113.9")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.BeginAsync(code)).StatusCode);
    }

    [Fact]
    public async Task Bootstrap_disabled_issues_no_code_and_refuses()
    {
        await using var host = await StartAsync(new() { ["signin:webauthn:bootstrapEnabled"] = "false" });

        Assert.Null(host.Sink.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.BeginAsync("anything")).StatusCode);
    }

    [Fact]
    public async Task A_request_from_another_origin_or_without_json_is_refused()
    {
        await using var host = await StartAsync();
        var code = await BootstrapCodeAsync(host);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.BeginAsync(code, origin: "https://evil.example")).StatusCode);
        using var plain = new HttpRequestMessage(HttpMethod.Post, "/signin/register/begin") { Content = new StringContent("{}", Encoding.UTF8, "text/plain") };
        plain.Headers.TryAddWithoutValidation("Origin", host.Origin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.SendAsync(plain, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_bad_attestation_stores_nothing_and_does_not_burn_the_code()
    {
        await using var host = await StartAsync();
        var auth = new TestAuthenticator();
        var code = await BootstrapCodeAsync(host);
        var begin = await Host.ReadAsync(await host.BeginAsync(code));
        var id = begin.GetProperty("id").GetString()!;

        var bad = await host.FinishAsync(id, auth.Attest(begin.GetProperty("challenge").GetString()!, "https://evil.example", rpId: "localhost"));

        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Null(await host.Store.FindByCredentialIdAsync(auth.CredentialId, Ct));
        var retry = await Host.ReadAsync(await host.BeginAsync(code));
        Assert.Equal(HttpStatusCode.OK, (await host.FinishAsync(retry.GetProperty("id").GetString()!, Attest(host, auth, retry))).StatusCode);
    }

    [Fact]
    public async Task A_registration_challenge_cannot_be_replayed()
    {
        await using var host = await StartAsync(signedInResolver: true);
        var auth = new TestAuthenticator();
        var begin = await Host.ReadAsync(await host.BeginAsync(null, principal: _owner.ToString()));
        var attestation = Attest(host, auth, begin);
        var id = begin.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await host.FinishAsync(id, attestation, _owner.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.FinishAsync(id, attestation, _owner.ToString())).StatusCode);
    }

    [Fact]
    public async Task A_signed_in_human_can_add_another_passkey_and_the_challenge_is_bound_to_them()
    {
        await using var host = await StartAsync(signedInResolver: true);
        var first = new TestAuthenticator();
        var second = new TestAuthenticator();
        var human = Guid.NewGuid();

        var begin1 = await Host.ReadAsync(await host.BeginAsync(null, "phone", principal: human.ToString()));
        Assert.Empty(begin1.GetProperty("excludeCredentials").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await host.FinishAsync(begin1.GetProperty("id").GetString()!, Attest(host, first, begin1), human.ToString())).StatusCode);

        var begin2 = await Host.ReadAsync(await host.BeginAsync(null, "key", principal: human.ToString()));
        Assert.Single(begin2.GetProperty("excludeCredentials").EnumerateArray());

        // Another signed-in principal cannot finish this challenge, and neither can an anonymous caller.
        var attestation2 = Attest(host, second, begin2);
        var id2 = begin2.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.FinishAsync(id2, attestation2, Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(1, await host.Store.CountAsync(human, Ct));

        // The challenge was burned by the failed attempt, so the real owner starts again.
        var begin3 = await Host.ReadAsync(await host.BeginAsync(null, "key", principal: human.ToString()));
        Assert.Equal(HttpStatusCode.OK, (await host.FinishAsync(begin3.GetProperty("id").GetString()!, Attest(host, second, begin3), human.ToString())).StatusCode);
        Assert.Equal(2, await host.Store.CountAsync(human, Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.FinishAsync("nope", attestation2)).StatusCode);
    }

    [Fact]
    public async Task Without_a_signed_in_adapter_or_code_nobody_can_register()
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.BeginAsync(null, principal: _owner.ToString())).StatusCode);
    }

    [Fact]
    public async Task A_failing_audit_log_rolls_the_registration_back()
    {
        await using var host = await StartAsync();
        var auth = new TestAuthenticator();
        var code = await BootstrapCodeAsync(host);
        var begin = await Host.ReadAsync(await host.BeginAsync(code));
        host.Audit.Fail = true;

        var res = await host.FinishAsync(begin.GetProperty("id").GetString()!, Attest(host, auth, begin));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Null(await host.Store.FindByCredentialIdAsync(auth.CredentialId, Ct));
    }

    [Fact]
    public async Task Registration_needs_an_audit_log()
    {
        await using var host = await StartAsync(withAudit: false);

        Assert.Null(host.Sink.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.BeginAsync("anything")).StatusCode);
    }

    [Fact]
    public async Task A_label_with_control_characters_or_too_long_is_refused()
    {
        await using var host = await StartAsync(signedInResolver: true);
        var who = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.BeginAsync(null, "bad\nlabel", who)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.BeginAsync(null, new string('x', 81), who)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.BeginAsync(null, null, who)).StatusCode);
    }

    [Fact]
    public async Task Recover_stays_501_and_nothing_is_served_when_sign_in_is_off()
    {
        await using var host = await StartAsync();
        var recover = await host.Client.PostAsync("/signin/recover", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.NotImplemented, recover.StatusCode);

        await using var off = await StartAsync(new() { ["signin:adminEnabled"] = "false" });
        Assert.Equal(HttpStatusCode.NotFound, (await off.BeginAsync("x")).StatusCode);
    }
}
