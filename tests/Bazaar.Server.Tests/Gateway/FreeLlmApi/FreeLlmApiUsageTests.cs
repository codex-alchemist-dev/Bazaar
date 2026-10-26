using System.Globalization;
using System.Net;
using System.Text;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Tests.Gateway.Adapters.OpenAi;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

public class FreeLlmApiUsageTests
{
    private const string _label = "work laptop";

    private static ProviderEndpoint Endpoint() => new(FreeLlmApiTestRig.BaseUrl, "model-x", FreeLlmApiTestRig.TestKey);

    /// <summary>A 200 reply with a JSON body.</summary>
    private static Func<CancellationToken, Task<HttpResponseMessage>> Json(string body) => _ =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    /// <summary>Every property value on the report and on its rows, as text.</summary>
    private static IEnumerable<string> ValuesOf(FreeLlmApiUsageReport report)
    {
        var records = new object[] { report }.Concat(report.ByPlatform).Concat(report.ByKey);
        return records.SelectMany(r => r.GetType().GetProperties().Select(p => Convert.ToString(p.GetValue(r), CultureInfo.InvariantCulture) ?? ""));
    }

    [Fact]
    public void The_usage_fixture_parses_to_the_expected_report()
    {
        var report = FreeLlmApiUsageParser.TryParse(FreeLlmApiTestRig.Fixture("usage.json"));

        Assert.NotNull(report);
        Assert.Equal("2026-10", report.Month);
        Assert.Equal(152, report.Requests);
        Assert.Equal(104000000, report.Tokens);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), report.ResetsAt);

        Assert.Equal(2, report.ByPlatform.Count);
        Assert.Equal(new FreeLlmApiPlatformUsage("opencode", 90, 80000000), report.ByPlatform[0]);
        Assert.Equal(new FreeLlmApiPlatformUsage("google", 62, 24000000), report.ByPlatform[1]);

        Assert.Equal(2, report.ByKey.Count);
        Assert.Equal(new FreeLlmApiKeyUsage(3, "opencode", true, 90, 80000000, null, 50000000), report.ByKey[0]);
        Assert.Equal(new FreeLlmApiKeyUsage(4, "google", true, 62, 24000000, 1000, null), report.ByKey[1]);
    }

    [Fact]
    public void A_malformed_usage_body_does_not_parse()
    {
        Assert.Null(FreeLlmApiUsageParser.TryParse(FreeLlmApiTestRig.Fixture("usage_malformed.json")));
    }

    [Fact]
    public void Invalid_json_does_not_parse_and_does_not_throw()
    {
        Assert.Null(FreeLlmApiUsageParser.TryParse("not json"));
    }

    [Fact]
    public void A_body_missing_byKey_does_not_parse()
    {
        const string body = """{ "month": "2026-10", "requests": 1, "tokens": 2, "byPlatform": [] }""";

        Assert.Null(FreeLlmApiUsageParser.TryParse(body));
    }

    [Fact]
    public async Task A_200_with_the_usage_body_returns_the_report_from_the_usage_url_with_the_bearer_key()
    {
        var handler = new StubHandler();
        handler.Reply(Json(FreeLlmApiTestRig.Fixture("usage.json")));
        var client = new FreeLlmApiUsageClient(FreeLlmApiTestRig.Client(handler));

        var result = await client.GetAsync(Endpoint(), TestContext.Current.CancellationToken);

        Assert.NotNull(result.Report);
        Assert.Null(result.Failure);
        Assert.Equal(152, result.Report.Requests);
        Assert.EndsWith("/v1/usage", handler.Requests[0].Url.ToString());
        Assert.Equal("Bearer TEST-KEY-NOT-A-SECRET", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task A_non_2xx_answer_fails_with_the_status_only()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Error(HttpStatusCode.Unauthorized, FreeLlmApiTestRig.Fixture("unauthorized_401.json")));
        var client = new FreeLlmApiUsageClient(FreeLlmApiTestRig.Client(handler));

        var result = await client.GetAsync(Endpoint(), TestContext.Current.CancellationToken);

        Assert.Null(result.Report);
        Assert.Equal("HTTP 401", result.Failure);
    }

    [Fact]
    public async Task A_200_with_a_malformed_body_fails_as_malformed()
    {
        var handler = new StubHandler();
        handler.Reply(Json(FreeLlmApiTestRig.Fixture("usage_malformed.json")));
        var client = new FreeLlmApiUsageClient(FreeLlmApiTestRig.Client(handler));

        var result = await client.GetAsync(Endpoint(), TestContext.Current.CancellationToken);

        Assert.Null(result.Report);
        Assert.Equal("malformed usage body", result.Failure);
    }

    [Fact]
    public async Task A_refused_connection_fails_as_unreachable()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Refused());
        var client = new FreeLlmApiUsageClient(FreeLlmApiTestRig.Client(handler));

        var result = await client.GetAsync(Endpoint(), TestContext.Current.CancellationToken);

        Assert.Null(result.Report);
        Assert.Equal("unreachable", result.Failure);
    }

    [Fact]
    public async Task A_client_timeout_fails_as_timeout()
    {
        var handler = new StubHandler();
        handler.Reply(Replies.Hang());
        var client = new FreeLlmApiUsageClient(FreeLlmApiTestRig.Client(handler, TimeSpan.FromMilliseconds(200)));

        var result = await client.GetAsync(Endpoint(), TestContext.Current.CancellationToken);

        Assert.Null(result.Report);
        Assert.Equal("timeout", result.Failure);
    }

    [Fact]
    public async Task The_operator_label_is_not_stored_in_any_returned_record()
    {
        // The fixture carries "label": "work laptop"; the key record has no property to hold it.
        Assert.DoesNotContain("Label", typeof(FreeLlmApiKeyUsage).GetProperties().Select(p => p.Name));

        var handler = new StubHandler();
        handler.Reply(Json(FreeLlmApiTestRig.Fixture("usage.json")));
        var client = new FreeLlmApiUsageClient(FreeLlmApiTestRig.Client(handler));

        var result = await client.GetAsync(Endpoint(), TestContext.Current.CancellationToken);

        Assert.NotNull(result.Report);
        Assert.DoesNotContain(_label, ValuesOf(result.Report));
    }
}
