using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.FreeLlmApi;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

public sealed class FreeLlmApiExpectedSetAuditorTests
{
    private const string _llama = "llama-3.3-70b";
    private const string _gemini = "gemini-2.5-flash";
    private const string _mistral = "mistral-large-2411";

    private static readonly string[] _missingOnly = ["nope"];
    private static readonly string[] _notReadyOnly = [_mistral, "kimi-k2"];

    [Fact]
    public void DirectCallInsideSetHasNoViolations()
    {
        var routing = Parse("chat_direct.headers");

        Assert.Empty(FreeLlmApiExpectedSetAuditor.Violations(Route(_llama), routing, null));
    }

    [Fact]
    public void FallbackInsideSetHasNoViolations()
    {
        var routing = Parse("chat_fallback.headers");

        Assert.Empty(FreeLlmApiExpectedSetAuditor.Violations(Route(_llama, _gemini), routing, null));
    }

    [Fact]
    public void FallbackOutsideSetIsAViolation()
    {
        var routing = Parse("chat_fallback_outside_set.headers");

        var violations = FreeLlmApiExpectedSetAuditor.Violations(Route(_llama, _gemini), routing, null);

        Assert.NotEmpty(violations);
        Assert.Contains(_mistral, violations);
    }

    [Fact]
    public void MalformedTrailIsAViolation()
    {
        var routing = Parse("chat_trail_malformed.headers");

        var violations = FreeLlmApiExpectedSetAuditor.Violations(Route(_llama), routing, null);

        Assert.Contains("(malformed fallback trail)", violations);
    }

    [Fact]
    public void BodyModelIsUsedWhenNoRoutingHeaders()
    {
        var routing = Parse("chat_no_routing.headers");

        Assert.Empty(FreeLlmApiExpectedSetAuditor.Violations(Route(_llama), routing, _llama));
    }

    [Fact]
    public void NoRoutingHeadersAndNoBodyModelReportsNoServedModel()
    {
        var routing = Parse("chat_no_routing.headers");

        var violations = FreeLlmApiExpectedSetAuditor.Violations(Route(_llama), routing, null);

        Assert.Contains("(no served model reported)", violations);
    }

    [Fact]
    public void ModelRefStyleExpectedEntryMatchesModelId()
    {
        var routing = Parse("chat_direct.headers");

        Assert.Empty(FreeLlmApiExpectedSetAuditor.Violations(Route("groq:" + _llama), routing, null));
    }

    [Fact]
    public void EmptyExpectedSetFailsClosed()
    {
        var routing = Parse("chat_direct.headers");

        var violations = FreeLlmApiExpectedSetAuditor.Violations(Route(), routing, null);

        Assert.Contains(_llama, violations);
    }

    [Fact]
    public void CoverageReportsMissingAndNotReadyModels()
    {
        var coverage = FreeLlmApiExpectedSetAuditor.Coverage(
            ["llama-3.3-70b", "mistral-large-2411", "kimi-k2", "nope"],
            Catalog());

        Assert.Equal(_missingOnly, coverage.Missing);
        Assert.Equal(_notReadyOnly, coverage.NotReady);
    }

    [Fact]
    public void NullExecutionStatusCountsAsReady()
    {
        List<FreeLlmApiModel> catalog = [new("llama-3.3-70b", null)];

        var coverage = FreeLlmApiExpectedSetAuditor.Coverage(["llama-3.3-70b"], catalog);

        Assert.Empty(coverage.Missing);
        Assert.Empty(coverage.NotReady);
    }

    [Fact]
    public void CoverageWithEmptyExpectedSetIsEmpty()
    {
        var coverage = FreeLlmApiExpectedSetAuditor.Coverage([], Catalog());

        Assert.Empty(coverage.Missing);
        Assert.Empty(coverage.NotReady);
    }

    private static FreeLlmApiRouting Parse(string headersFile)
    {
        using var response = FreeLlmApiTestRig.ResponseWithHeaders(headersFile);
        return FreeLlmApiRouting.Parse(response.Headers);
    }

    private static GwRoute Route(params string[] expected) =>
        new("custom/code", RouteStrategy.Priority, [], expected);

    private static List<FreeLlmApiModel> Catalog() =>
    [
        new("llama-3.3-70b", "ready"),
        new("gemini-2.5-flash", "ready"),
        new("mistral-large-2411", "exhausted"),
        new("kimi-k2", "needsKey"),
    ];
}
