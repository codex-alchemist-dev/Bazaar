using System.Net;

namespace Bazaar.Client.SignIn.Tests;

public class SignInServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static (HttpSignInService Service, FakeHandler Handler, InMemorySessionStore Store) Make(Func<HttpRequestMessage, string?, HttpResponseMessage> answer)
    {
        var handler = new FakeHandler(answer);
        var store = new InMemorySessionStore();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8080/") };
        return (new HttpSignInService(http, store, "windows", "0.1.0", new FixedTime()), handler, store);
    }

    private const string Tokens = """{"access_token":"a1","token_type":"Bearer","expires_in":900,"refresh_token":"r1"}""";

    [Theory]
    [InlineData("abcDEF123-_", "abcDEF123-_")]
    [InlineData("  abc123  ", "abc123")]
    [InlineData("bazaar://signin?code=abc123", "abc123")]
    [InlineData("BAZAAR://SIGNIN?code=a%2Db", "a-b")]
    [InlineData("bazaar://signin?x=1&code=zz9", "zz9")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("bazaar://signin", null)]
    [InlineData("bazaar://other?code=abc", null)]
    [InlineData("https://evil.example/?code=abc", null)]
    [InlineData("has space", null)]
    public void Handoff_code_is_read_from_a_bare_code_or_the_app_address(string? input, string? expected) =>
        Assert.Equal(expected, HandoffCode.TryRead(input));

    [Fact]
    public async Task Redeeming_a_code_saves_the_session_and_sends_the_device_class()
    {
        var (service, handler, store) = Make((_, _) => FakeHandler.Json(HttpStatusCode.OK, Tokens));

        var result = await service.RedeemHandoffAsync("bazaar://signin?code=abc123", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var saved = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("a1", saved!.AccessToken);
        Assert.Equal("r1", saved.RefreshToken);
        Assert.Equal(_now.AddSeconds(900), saved.AccessTokenExpiresAt);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/auth/handoff", request.Path);
        Assert.Contains("\"code\":\"abc123\"", request.Body);
        Assert.Contains("\"device_class\":\"windows\"", request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_refused_code_gives_one_generic_answer_and_saves_nothing(HttpStatusCode status)
    {
        var (service, _, store) = Make((_, _) => FakeHandler.Json(status, """{"error":"x"}"""));

        var result = await service.RedeemHandoffAsync("abc123", TestContext.Current.CancellationToken);

        Assert.Equal(SignInOutcome.Refused, result.Outcome);
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_server_that_is_off_is_unavailable_not_refused()
    {
        var (service, _, _) = Make((_, _) => FakeHandler.Json(HttpStatusCode.NotFound, "{}"));
        Assert.Equal(SignInOutcome.Unavailable, (await service.RedeemHandoffAsync("abc123", TestContext.Current.CancellationToken)).Outcome);

        var (broken, _, _) = Make((_, _) => throw new HttpRequestException("down"));
        Assert.Equal(SignInOutcome.Unavailable, (await broken.RedeemHandoffAsync("abc123", TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task An_incomplete_token_reply_is_unavailable_and_saves_nothing()
    {
        var (service, _, store) = Make((_, _) => FakeHandler.Json(HttpStatusCode.OK, """{"access_token":"","refresh_token":"r"}"""));

        Assert.Equal(SignInOutcome.Unavailable, (await service.RedeemHandoffAsync("abc123", TestContext.Current.CancellationToken)).Outcome);
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_bad_code_is_rejected_before_any_request()
    {
        var (service, handler, _) = Make((_, _) => FakeHandler.Json(HttpStatusCode.OK, Tokens));

        Assert.Equal(SignInOutcome.InvalidCode, (await service.RedeemHandoffAsync("not a code!", TestContext.Current.CancellationToken)).Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Refresh_replaces_the_session_and_a_refusal_clears_it()
    {
        var accept = true;
        var (service, handler, store) = Make((_, _) => accept
            ? FakeHandler.Json(HttpStatusCode.OK, Tokens)
            : FakeHandler.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));
        await store.SaveAsync(new ClientSession("old", "oldr", _now), TestContext.Current.CancellationToken);

        Assert.True((await service.RefreshAsync(TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal("r1", (await store.LoadAsync(TestContext.Current.CancellationToken))!.RefreshToken);
        Assert.Contains("\"refresh_token\":\"oldr\"", handler.Requests[0].Body);

        accept = false;
        Assert.Equal(SignInOutcome.Refused, (await service.RefreshAsync(TestContext.Current.CancellationToken)).Outcome);
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refresh_without_a_session_does_not_call_the_server()
    {
        var (service, handler, _) = Make((_, _) => FakeHandler.Json(HttpStatusCode.OK, Tokens));

        Assert.Equal(SignInOutcome.Refused, (await service.RefreshAsync(TestContext.Current.CancellationToken)).Outcome);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ServerReachability.Ready)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ServerReachability.NotReady)]
    public async Task Probe_reads_the_readiness_route(HttpStatusCode status, ServerReachability expected)
    {
        var (service, handler, _) = Make((_, _) => FakeHandler.Json(status, "{}"));

        Assert.Equal(expected, await service.ProbeAsync(TestContext.Current.CancellationToken));
        Assert.Equal("/health/ready", handler.Requests[0].Path);
    }

    [Fact]
    public async Task Probe_reports_an_unreachable_server()
    {
        var (service, _, _) = Make((_, _) => throw new HttpRequestException("down"));
        Assert.Equal(ServerReachability.Unreachable, await service.ProbeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_sign_in_page_is_the_native_audience_page_on_the_company_server()
    {
        var (service, _, _) = Make((_, _) => FakeHandler.Json(HttpStatusCode.OK, "{}"));
        Assert.Equal("http://127.0.0.1:8080/signin?audience=native", service.SignInPageAddress.ToString());
    }

    [Fact]
    public void A_session_never_prints_its_secrets() =>
        Assert.DoesNotContain("secret-", new ClientSession("secret-a", "secret-r", _now).ToString());
}
