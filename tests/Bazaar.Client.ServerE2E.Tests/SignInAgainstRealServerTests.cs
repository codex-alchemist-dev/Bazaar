using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bazaar.Client.SignIn;

namespace Bazaar.Client.ServerE2E.Tests;

/// <summary>
/// Real sign-in end to end (doc 32 sections 4 and 5): the real host process, the sign-in page's challenge and assert routes, the
/// handoff code, <c>POST /auth/handoff</c>, <c>GET /auth/whoami</c> and <c>POST /auth/refresh</c>, driven by the client's own
/// sign-in and who-am-I services. The passkey check is the host's development verifier (the assertion is the dev token), since no
/// WebAuthn adapter exists yet.
/// </summary>
public sealed class SignInAgainstRealServerTests(RealServerFixture server) : IClassFixture<RealServerFixture>
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_client_signs_in_through_the_handoff_and_the_server_knows_who_it_is()
    {
        Assert.SkipWhen(Bazaar.Testing.Postgres.PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

        using var http = new HttpClient { BaseAddress = server.HttpUri };
        var store = new InMemorySessionStore();
        var signIn = new HttpSignInService(http, store, "linux", "e2e");

        Assert.Equal(ServerReachability.Ready, await signIn.ProbeAsync(_ct));

        var redirect = await GetHandoffRedirectAsync(http, server.Token);
        Assert.StartsWith("bazaar://signin?code=", redirect, StringComparison.Ordinal);

        var result = await signIn.RedeemHandoffAsync(redirect, _ct);
        Assert.True(result.Succeeded, result.Message + "\nHost output:\n" + server.HostOutput);

        var session = await signIn.CurrentSessionAsync(_ct);
        Assert.NotNull(session);

        var who = await new HttpWhoAmIClient(http).WhoAmIAsync(session, _ct);
        Assert.Equal(WhoAmIOutcome.Found, who.Outcome);
        Assert.Equal(server.OwnerId, who.Person!.HumanId);
        Assert.Equal("E2E Owner", who.Person.DisplayName);

        // The code is single use.
        var again = await signIn.RedeemHandoffAsync(redirect, _ct);
        Assert.Equal(SignInOutcome.Refused, again.Outcome);

        // The refresh secret rotates the pair, and the new access token still names the same person.
        var refreshed = await signIn.RefreshAsync(_ct);
        Assert.True(refreshed.Succeeded, refreshed.Message + "\nHost output:\n" + server.HostOutput);
        var after = await signIn.CurrentSessionAsync(_ct);
        Assert.NotEqual(session.RefreshToken, after!.RefreshToken);
        var whoAfter = await new HttpWhoAmIClient(http).WhoAmIAsync(after, _ct);
        Assert.Equal(server.OwnerId, whoAfter.Person!.HumanId);
    }

    [Fact]
    public async Task A_wrong_assertion_gets_no_code()
    {
        Assert.SkipWhen(Bazaar.Testing.Postgres.PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

        using var http = new HttpClient { BaseAddress = server.HttpUri };
        var (status, _) = await AssertAsync(http, "not-the-token");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Whoami_refuses_a_token_the_server_did_not_issue()
    {
        Assert.SkipWhen(Bazaar.Testing.Postgres.PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

        using var http = new HttpClient { BaseAddress = server.HttpUri };
        var fake = new ClientSession("not.a.token", "x", DateTimeOffset.UtcNow.AddMinutes(5));
        var who = await new HttpWhoAmIClient(http).WhoAmIAsync(fake, _ct);
        Assert.Equal(WhoAmIOutcome.InvalidToken, who.Outcome);
    }

    private async Task<string> GetHandoffRedirectAsync(HttpClient http, string assertion)
    {
        var (status, body) = await AssertAsync(http, assertion);
        Assert.True(status == HttpStatusCode.OK, $"assert answered {(int)status}: {body}\nHost output:\n{server.HostOutput}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("redirect").GetString()!;
    }

    private async Task<(HttpStatusCode Status, string Body)> AssertAsync(HttpClient http, string assertion)
    {
        var origin = server.HttpUri.GetLeftPart(UriPartial.Authority);

        using var challengeRequest = new HttpRequestMessage(HttpMethod.Post, "signin/challenge") { Content = Json("{}") };
        challengeRequest.Headers.Add("Origin", origin);
        using var challengeReply = await http.SendAsync(challengeRequest, _ct);
        Assert.True(challengeReply.StatusCode == HttpStatusCode.OK, $"challenge answered {(int)challengeReply.StatusCode}\nHost output:\n{server.HostOutput}");
        var challengeId = (await challengeReply.Content.ReadFromJsonAsync<JsonElement>(_ct)).GetProperty("id").GetString();

        var payload = JsonSerializer.Serialize(new { challengeId, assertion, audience = "native" });
        using var assertRequest = new HttpRequestMessage(HttpMethod.Post, "signin/assert?audience=native") { Content = Json(payload) };
        assertRequest.Headers.Add("Origin", origin);
        using var reply = await http.SendAsync(assertRequest, _ct);
        return (reply.StatusCode, await reply.Content.ReadAsStringAsync(_ct));
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
}
