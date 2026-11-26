using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.SignIn.Handoff;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.SignIn.Tests;

public sealed class SignInHostTests
{
    private static readonly SignInIdentity _who = new(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid());

    private sealed class FakeVerifier : IPasskeyAssertionVerifier
    {
        public int Calls { get; private set; }

        public Task<SignInIdentity?> VerifyAsync(string challenge, string assertionJson, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<SignInIdentity?>(challenge.Length > 0 && assertionJson == "good" ? _who : null);
        }
    }

    private sealed class FailingAudit : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => throw new InvalidOperationException("down");
    }

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public required HttpClient Client { get; init; }

        public string Origin => $"http://{Client.BaseAddress!.Authority}";

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(TestContext.Current.CancellationToken);
            await App.DisposeAsync();
        }

        public Task<HttpResponseMessage> PostAsync(string path, string json, bool origin = true)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (origin)
            {
                req.Headers.TryAddWithoutValidation("Origin", Origin);
            }

            return Client.SendAsync(req, TestContext.Current.CancellationToken);
        }

        public async Task<JsonElement> ChallengeAsync()
        {
            var res = await PostAsync("/signin/challenge", "{}");
            return JsonElement.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Clone();
        }
    }

    private static async Task<Host> StartAsync(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        new SignInComponent().AddServices(builder.Services, config);
        builder.Services.AddSingleton<FakeVerifier>();
        builder.Services.AddSingleton<IPasskeyAssertionVerifier>(sp => sp.GetRequiredService<FakeVerifier>());
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        app.MapBazaarSignIn();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var uri = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = uri };
        return new Host { App = app, Client = client };
    }

    private static string AssertBody(JsonElement challenge, string assertion, string? audience = null) =>
        JsonSerializer.Serialize(new { challengeId = challenge.GetProperty("id").GetString(), assertion, audience });

    [Fact]
    public async Task Default_serves_the_shell_for_the_admin_audience_only()
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/signin?audience=admin", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/signin", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/signin?audience=native", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Nothing_is_served_when_both_switches_are_off()
    {
        await using var host = await StartAsync(new() { ["signin:adminEnabled"] = "false" });

        foreach (var path in new[] { "/signin", "/signin?audience=admin", "/signin/assets/signin.js", "/signin/register" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync("/signin/challenge", "{}")).StatusCode);
    }

    [Fact]
    public async Task Native_audience_needs_signin_enabled()
    {
        await using var host = await StartAsync(new() { ["signin:enabled"] = "true", ["signin:adminEnabled"] = "false" });

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/signin", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/signin?audience=admin", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Shell_shows_the_encoded_server_name_and_carries_the_csp()
    {
        await using var host = await StartAsync(new() { ["signin:displayName"] = "Acme <b>&" });
        var res = await host.Client.GetAsync("/signin?audience=admin", TestContext.Current.CancellationToken);
        var html = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Acme &lt;b&gt;&amp;", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Equal(SignInEndpoints.ContentSecurityPolicy, res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("script-src 'self'", SignInEndpoints.ContentSecurityPolicy);
        Assert.DoesNotContain("unsafe-inline", SignInEndpoints.ContentSecurityPolicy);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Admin_audience_success_redirects_to_the_panel_and_the_code_redeems_once_for_admin()
    {
        await using var host = await StartAsync();
        var challenge = await host.ChallengeAsync();
        var res = await host.PostAsync("/signin/assert?audience=admin", AssertBody(challenge, "good"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var redirect = JsonElement.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).GetProperty("redirect").GetString()!;
        Assert.StartsWith("/admin/session/redeem?code=", redirect);
        var code = Uri.UnescapeDataString(redirect["/admin/session/redeem?code=".Length..]);

        var redeemer = host.App.Services.GetRequiredService<Bazaar.Server.WebAdmin.IAdminHandoffRedeemer>();
        var identity = await redeemer.RedeemAsync(code, TestContext.Current.CancellationToken);
        Assert.Equal(_who.PrincipalId, identity?.PrincipalId);
        Assert.Null(await redeemer.RedeemAsync(code, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Native_audience_success_redirects_to_the_native_scheme_and_not_redeemable_by_the_panel()
    {
        await using var host = await StartAsync(new() { ["signin:enabled"] = "true" });
        var challenge = await host.ChallengeAsync();
        var res = await host.PostAsync("/signin/assert", AssertBody(challenge, "good"));

        var redirect = JsonElement.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).GetProperty("redirect").GetString()!;
        Assert.StartsWith("bazaar://signin?code=", redirect);
        var code = Uri.UnescapeDataString(redirect["bazaar://signin?code=".Length..]);

        Assert.Null(await host.App.Services.GetRequiredService<Bazaar.Server.WebAdmin.IAdminHandoffRedeemer>().RedeemAsync(code, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Native_audience_is_refused_when_only_the_panel_is_on()
    {
        await using var host = await StartAsync();
        var challenge = await host.ChallengeAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync("/signin/assert", AssertBody(challenge, "good", "native"))).StatusCode);
    }

    [Fact]
    public async Task Every_failure_has_the_same_status_and_body()
    {
        await using var host = await StartAsync();
        var challenge = await host.ChallengeAsync();
        var bad = await host.PostAsync("/signin/assert?audience=admin", AssertBody(challenge, "bad"));
        var replay = await host.PostAsync("/signin/assert?audience=admin", AssertBody(challenge, "good"));
        var garbage = await host.PostAsync("/signin/assert?audience=admin", "not json");
        var noOrigin = await host.PostAsync("/signin/assert?audience=admin", AssertBody(await host.ChallengeAsync(), "good"), origin: false);
        var badAudience = await host.PostAsync("/signin/assert?audience=nope", AssertBody(await host.ChallengeAsync(), "good"));

        var bodies = new List<string>();
        foreach (var r in new[] { bad, replay, garbage, noOrigin, badAudience })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            bodies.Add(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        Assert.Single(bodies.Distinct());
        Assert.Contains("Sign-in failed.", bodies[0]);
    }

    [Fact]
    public async Task Verifier_runs_for_every_assert_so_failures_share_one_shape()
    {
        await using var host = await StartAsync();
        var verifier = host.App.Services.GetRequiredService<FakeVerifier>();
        await host.PostAsync("/signin/assert?audience=admin", "not json");
        await host.PostAsync("/signin/assert?audience=admin", AssertBody(await host.ChallengeAsync(), "bad"));

        Assert.Equal(2, verifier.Calls);
    }

    [Fact]
    public async Task Default_verifier_denies_everyone()
    {
        await using var host = await StartAsync(configure: s =>
        {
            s.AddSingleton<IPasskeyAssertionVerifier, DenyAllPasskeyVerifier>();
        });
        var challenge = await host.ChallengeAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync("/signin/assert?audience=admin", AssertBody(challenge, "good"))).StatusCode);
    }

    [Fact]
    public async Task A_refusing_audit_sink_fails_the_sign_in_closed()
    {
        await using var host = await StartAsync(configure: s => s.AddSingleton<IAuditLog, FailingAudit>());
        var challenge = await host.ChallengeAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync("/signin/assert?audience=admin", AssertBody(challenge, "good"))).StatusCode);
    }

    [Theory]
    [InlineData("/signin/register")]
    [InlineData("/signin/recover")]
    public async Task Bare_register_and_recover_are_not_implemented(string path)
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.NotImplemented, (await host.Client.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await host.PostAsync(path, "{}")).StatusCode);
    }

    private sealed class FakeEnrolment : IPasskeyEnrolment
    {
        public string? LastToken { get; private set; }

        public Task<string?> BeginAsync(string bearerToken, CancellationToken ct = default)
        {
            LastToken = bearerToken;
            return Task.FromResult<string?>(bearerToken == "good" ? "{\"challenge\":\"abc\"}" : null);
        }

        public Task<bool> CompleteAsync(string bearerToken, string attestationJson, CancellationToken ct = default) =>
            Task.FromResult(bearerToken == "good" && attestationJson == "att");
    }

    private static Task<HttpResponseMessage> RegisterAsync(Host host, string path, string json, string? bearer, bool origin = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (origin)
        {
            req.Headers.TryAddWithoutValidation("Origin", host.Origin);
        }

        if (bearer is not null)
        {
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        }

        return host.Client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Register_routes_are_501_without_an_enrolment_service()
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.NotImplemented, (await RegisterAsync(host, "/signin/register/options", "{}", "good")).StatusCode);
        Assert.Equal(HttpStatusCode.NotImplemented, (await RegisterAsync(host, "/signin/register/complete", "{}", "good")).StatusCode);
    }

    [Fact]
    public async Task Register_options_and_complete_run_for_a_valid_token()
    {
        await using var host = await StartAsync(configure: s => s.AddSingleton<IPasskeyEnrolment, FakeEnrolment>());

        var options = await RegisterAsync(host, "/signin/register/options", "{}", "good");
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        Assert.Contains("abc", await options.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var done = await RegisterAsync(host, "/signin/register/complete", "{\"attestation\":\"att\"}", "good");
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }

    [Theory]
    [InlineData("bad", true, "{\"attestation\":\"att\"}")]
    [InlineData(null, true, "{\"attestation\":\"att\"}")]
    [InlineData("good", false, "{\"attestation\":\"att\"}")]
    [InlineData("good", true, "{\"attestation\":\"wrong\"}")]
    [InlineData("good", true, "{}")]
    [InlineData("good", true, "not json")]
    public async Task Register_refusals_are_one_generic_401(string? bearer, bool origin, string body)
    {
        await using var host = await StartAsync(configure: s => s.AddSingleton<IPasskeyEnrolment, FakeEnrolment>());

        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(host, "/signin/register/complete", body, bearer, origin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RegisterAsync(host, "/signin/register/options", "{}", bearer is "good" ? "bad" : bearer, origin)).StatusCode);
    }

    [Fact]
    public async Task Register_routes_take_post_only()
    {
        await using var host = await StartAsync(configure: s => s.AddSingleton<IPasskeyEnrolment, FakeEnrolment>());

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.Client.GetAsync("/signin/register/options", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Methods_and_unknown_paths_are_refused()
    {
        await using var host = await StartAsync();

        var res = await host.Client.PostAsync("/signin", new StringContent("{}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.Client.GetAsync("/signin/assert", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/signin/other", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/signin/assets/../x", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Challenge_needs_a_matching_origin()
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync("/signin/challenge", "{}", origin: false)).StatusCode);
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("assets/signin.js")]
    [InlineData("assets/signin.css")]
    public void Assets_have_no_external_urls_or_inline_code(string path)
    {
        Assert.True(Http.SignInAssets.TryGet(path, out var bytes, out _));
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("http://", text);
        Assert.DoesNotContain("https://", text);
        Assert.DoesNotContain("//cdn", text);
        Assert.DoesNotContain("<style", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick=", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eval(", text);
        if (path == "index.html")
        {
            Assert.DoesNotContain("<script>", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<script src=\"/signin/assets/signin.js\" defer>", text);
        }
    }

    [Fact]
    public void Component_manifest_matches_the_design()
    {
        var m = SignInComponent.ManifestDefinition;

        Assert.Equal("sign-in", m.Id);
        Assert.Equal(Bazaar.Core.ComponentKind.Feature, m.Kind);
        Assert.Empty(m.DependsOn);
        Assert.Equal(["audit"], m.Soft);
        Assert.Equal("signin", SignInRateLimitBuckets.SignIn);
    }
}
