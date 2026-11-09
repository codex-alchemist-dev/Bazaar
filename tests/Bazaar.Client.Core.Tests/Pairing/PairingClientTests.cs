using System.Net;
using System.Text;
using Bazaar.Client.Core.Pairing;

namespace Bazaar.Client.Core.Tests.Pairing;

public class PairingClientTests
{
    private static readonly Uri _base = new("http://pair.test:8080/");

    [Fact]
    public async Task Start_posts_the_user_code_and_returns_the_device_code_and_interval()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"device_code":"dev-1","interval":5}"""));

        var auth = await Client(handler, (_, _) => Task.CompletedTask).StartAsync("ABCD2345", "Dom laptop", "windows", TestContext.Current.CancellationToken);

        Assert.Equal(new DeviceAuthorization("dev-1", 5), auth);
        Assert.Equal("/pair/device-authorization", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"user_code\":\"ABCD2345\"", handler.Bodies[0]);
        Assert.Contains("\"device_class\":\"windows\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task Poll_waits_through_pending_and_slow_down_then_returns_the_grant()
    {
        var handler = new ScriptedHandler(
            _ => Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            _ => Json(HttpStatusCode.BadRequest, """{"error":"slow_down"}"""),
            _ => Json(HttpStatusCode.OK, """{"access_token":"at-1","refresh_token":"rt-1","expires_in":900}"""));
        var delays = new List<TimeSpan>();

        var grant = await Client(handler, (d, _) => { delays.Add(d); return Task.CompletedTask; })
            .PollAsync(new DeviceAuthorization("dev-1", 5), TestContext.Current.CancellationToken);

        Assert.Equal(new PairingGrant("at-1", "rt-1", 900), grant);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], delays);
        Assert.All(handler.Requests, request => Assert.Equal("/pair/token", request.RequestUri!.AbsolutePath));
        Assert.All(handler.Bodies, body => Assert.Contains("urn:ietf:params:oauth:grant-type:device_code", body));
    }

    [Theory]
    [InlineData("expired_token")]
    [InlineData("access_denied")]
    public async Task Terminal_errors_end_pairing_with_the_error_code(string code)
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.BadRequest, $$"""{"error":"{{code}}"}"""));

        var error = await Assert.ThrowsAsync<PairingException>(() =>
            Client(handler, (_, _) => Task.CompletedTask).PollAsync(new DeviceAuthorization("dev-1", 5), TestContext.Current.CancellationToken));

        Assert.Equal(code, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_non_json_error_body_ends_pairing_as_malformed()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("<html>oops</html>", Encoding.UTF8, "text/html"),
        });

        var error = await Assert.ThrowsAsync<PairingException>(() =>
            Client(handler, (_, _) => Task.CompletedTask).PollAsync(new DeviceAuthorization("dev-1", 5), TestContext.Current.CancellationToken));

        Assert.Equal("malformed_response", error.Code);
    }

    [Fact]
    public void A_grant_becomes_an_endpoint_with_the_access_token()
    {
        var endpoint = PairingClient.ToEndpoint(_base, new PairingGrant("at-9", "rt-9", 900));

        Assert.Equal(_base, endpoint.BaseAddress);
        Assert.Equal("at-9", endpoint.Credential);
    }

    private static PairingClient Client(HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task> delay)
    {
        var http = new HttpClient(handler) { BaseAddress = _base };
        return new PairingClient(http, delay);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers each request with the next scripted response and records what was sent.</summary>
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _next;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return script[_next++](request);
        }
    }
}
