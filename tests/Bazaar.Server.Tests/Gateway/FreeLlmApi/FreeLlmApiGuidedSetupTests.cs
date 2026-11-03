using System.Net;
using System.Text;
using Bazaar.Server.Gateway.FreeLlmApi;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Tests.Gateway.FreeLlmApi;

/// <summary>
/// FreeLLMAPI guided setup (steps 2 to 7). Every call goes to a fake handler, so no network is used. The planted key is a
/// placeholder that must never appear in any result string.
/// </summary>
public sealed class FreeLlmApiGuidedSetupTests
{
    private const string _keyReference = "env:FREELLMAPI_SETUP_TEST_KEY";
    private const string _plantedKey = "freellmapi-PLANTED-SECRET-DO-NOT-LEAK";
    private const string _models = """{"data":[{"id":"llama-3.3-70b","execution_status":"ready"},{"id":"gemini-2.5-flash","execution_status":"ready"},{"id":"needs-key-model","execution_status":"needsKey"}]}""";
    private const string _usage = """{"month":"2026-11","requests":10,"tokens":1000,"byPlatform":[],"byKey":[]}""";
    private const string _chat = """{"model":"llama-3.3-70b","choices":[]}""";

    private static readonly string[] _allWarningIds = FreeLlmApiWarnings.All.Select(w => w.Id).ToArray();
    private static readonly string[] _probeMethods = ["GET", "GET", "POST"];
    private static readonly string[] _probeUrls =
    [
        "http://localhost:3001/v1/models",
        "http://localhost:3001/v1/usage",
        "http://localhost:3001/v1/chat/completions",
    ];
    private static readonly string[] _probePaths = ["/v1/models", "/v1/usage", "/v1/chat/completions"];
    private static readonly string[] _candidateIds = ["llama-3.3-70b", "gemini-2.5-flash", "needs-key-model"];

    private static FreeLlmApiSetupRequest Acknowledged(
        IReadOnlyList<string>? preset = null,
        FreeLlmApiRoutingChoice? routing = null,
        string? baseUrl = null,
        string? keyReference = _keyReference) =>
        new(_allWarningIds, baseUrl, keyReference, preset, routing);

    private static (FreeLlmApiGuidedSetup Setup, SetupHandler Handler, InMemoryWarningAcknowledgementStore Store) Build(
        ISecretResolver? resolver = null)
    {
        var handler = new SetupHandler();
        handler.Route("GET", "/v1/models", HttpStatusCode.OK, _models);
        handler.Route("GET", "/v1/usage", HttpStatusCode.OK, _usage);
        handler.Route("POST", "/v1/chat/completions", HttpStatusCode.OK, _chat, new Dictionary<string, string>
        {
            ["X-Routed-Via"] = "groq/llama-3.3-70b",
        });

        var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
        var store = new InMemoryWarningAcknowledgementStore();
        var setup = new FreeLlmApiGuidedSetup(http, resolver ?? new FixedResolver(_plantedKey), store);
        return (setup, handler, store);
    }

    private static IEnumerable<string> TextOf(FreeLlmApiSetupReport report) =>
        report.Steps.Select(s => s.Message)
            .Concat(report.Calls.SelectMany(c => new[] { c.Method, c.Url }))
            .Concat(report.Candidates.Select(c => c.Id))
            .Concat(report.Violations);

    [Fact]
    public async Task Warnings_not_acknowledged_block_the_probe()
    {
        var (setup, handler, _) = Build();

        var report = await setup.RunAsync(new FreeLlmApiSetupRequest(KeyReference: _keyReference), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[1].Status);
        Assert.Equal(FreeLlmApiStepStatus.Skipped, report.Steps[2].Status);
        Assert.Equal(FreeLlmApiStepStatus.Skipped, report.Steps[3].Status);
        Assert.Empty(handler.Sent);
        Assert.Empty(report.Calls);
        Assert.False(report.Completed);
    }

    [Fact]
    public async Task One_unticked_warning_still_blocks_the_probe()
    {
        var (setup, handler, _) = Build();
        var request = new FreeLlmApiSetupRequest(_allWarningIds.Skip(1).ToList(), null, _keyReference);

        var report = await setup.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[1].Status);
        Assert.Contains("1 of", report.Steps[1].Message, StringComparison.Ordinal);
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Acknowledgements_are_recorded_with_a_timestamp_and_the_first_one_is_kept()
    {
        var store = new InMemoryWarningAcknowledgementStore();
        var first = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        var second = first.AddHours(1);

        await store.RecordAsync("pin-version", first, TestContext.Current.CancellationToken);
        await store.RecordAsync("pin-version", second, TestContext.Current.CancellationToken);

        Assert.Equal(first, await store.GetAsync("pin-version", TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync("loopback-bind", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ticking_all_warnings_records_each_one()
    {
        var (setup, _, store) = Build();

        await setup.RunAsync(new FreeLlmApiSetupRequest(_allWarningIds), TestContext.Current.CancellationToken);

        foreach (var id in _allWarningIds)
        {
            Assert.NotNull(await store.GetAsync(id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task An_unknown_warning_id_fails_step_2_and_is_not_echoed()
    {
        var (setup, handler, _) = Build();
        var request = new FreeLlmApiSetupRequest([_plantedKey], null, _keyReference);

        var report = await setup.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[1].Status);
        Assert.Empty(handler.Sent);
        AssertNoKey(report);
    }

    [Fact]
    public async Task A_literal_key_is_refused_before_any_call_and_never_shown()
    {
        var (setup, handler, _) = Build();

        var report = await setup.RunAsync(Acknowledged(keyReference: _plantedKey), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[2].Status);
        Assert.Equal(FreeLlmApiStepStatus.Skipped, report.Steps[3].Status);
        Assert.Empty(handler.Sent);
        AssertNoKey(report);
    }

    [Fact]
    public async Task A_missing_key_reference_is_refused()
    {
        var (setup, handler, _) = Build();

        var report = await setup.RunAsync(Acknowledged(keyReference: "  "), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[2].Status);
        Assert.Empty(handler.Sent);
    }

    [Theory]
    [InlineData("ftp://localhost:3001/v1")]
    [InlineData("localhost:3001/v1")]
    [InlineData("http://localhost:3001/v1?debug=1")]
    [InlineData("http://user:freellmapi-PLANTED-SECRET-DO-NOT-LEAK@localhost:3001/v1")]
    public async Task A_bad_base_url_is_refused_and_its_text_is_not_echoed(string baseUrl)
    {
        var (setup, handler, _) = Build();

        var report = await setup.RunAsync(Acknowledged(baseUrl: baseUrl), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[2].Status);
        Assert.Empty(handler.Sent);
        AssertNoKey(report);
    }

    [Fact]
    public async Task A_blank_base_url_uses_the_default()
    {
        var (setup, _, _) = Build();

        var report = await setup.RunAsync(Acknowledged(), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Ok, report.Steps[2].Status);
        Assert.Equal("http://localhost:3001/v1/models", report.Calls[0].Url);
    }

    [Fact]
    public async Task The_probe_runs_models_then_usage_then_one_test_message()
    {
        var (setup, handler, _) = Build();

        var report = await setup.RunAsync(Acknowledged(), TestContext.Current.CancellationToken);

        Assert.Equal(_probeMethods, report.Calls.Select(c => c.Method).ToArray());
        Assert.Equal(_probeUrls, report.Calls.Select(c => c.Url).ToArray());
        Assert.Equal(_probePaths, handler.Sent.Select(s => s.Path).ToArray());
        Assert.Equal(FreeLlmApiStepStatus.Ok, report.Steps[3].Status);
    }

    [Fact]
    public async Task A_failed_usage_probe_stops_the_run_before_the_test_message()
    {
        var (setup, handler, _) = Build();
        handler.Route("GET", "/v1/usage", HttpStatusCode.InternalServerError, "{}");

        var report = await setup.RunAsync(Acknowledged(), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[3].Status);
        Assert.Equal("GET http://localhost:3001/v1/usage failed: HTTP 500.", report.Steps[3].Message);
        Assert.Equal(2, report.Calls.Count);
        Assert.DoesNotContain(handler.Sent, s => s.Path == "/v1/chat/completions");
        Assert.All(report.Steps.Skip(4), step => Assert.Equal(FreeLlmApiStepStatus.Skipped, step.Status));
    }

    [Fact]
    public async Task A_malformed_model_list_fails_step_4_before_usage_is_read()
    {
        var (setup, handler, _) = Build();
        handler.Route("GET", "/v1/models", HttpStatusCode.OK, """{"items":[]}""");

        var report = await setup.RunAsync(Acknowledged(), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[3].Status);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task A_key_reference_that_does_not_resolve_fails_step_4_without_a_call()
    {
        var (setup, handler, _) = Build(new ThrowingResolver());

        var report = await setup.RunAsync(Acknowledged(), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[3].Status);
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Without_a_ticked_preset_the_run_waits_at_step_5_with_the_candidates()
    {
        var (setup, _, _) = Build();

        var report = await setup.RunAsync(Acknowledged(), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Skipped, report.Steps[4].Status);
        Assert.Equal(_candidateIds, report.Candidates.Select(c => c.Id).ToArray());
        Assert.Equal(3, report.Calls.Count);
    }

    [Fact]
    public async Task A_ticked_preset_and_routing_complete_the_run_and_pin_the_self_test_to_the_first_model()
    {
        var (setup, handler, _) = Build();

        var report = await setup.RunAsync(
            Acknowledged(["llama-3.3-70b", "gemini-2.5-flash"], FreeLlmApiRoutingChoice.Custom),
            TestContext.Current.CancellationToken);

        Assert.True(report.Completed);
        Assert.Contains("custom/*", report.Steps[5].Message, StringComparison.Ordinal);
        Assert.Empty(report.Violations);
        Assert.Equal(4, report.Calls.Count);
        Assert.Contains("\"model\":\"llama-3.3-70b\"", handler.Sent[3].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("needs-key-model")]
    [InlineData("not-in-the-catalogue")]
    public async Task A_preset_with_an_unusable_model_fails_step_5(string modelId)
    {
        var (setup, _, _) = Build();

        var report = await setup.RunAsync(Acknowledged([modelId], FreeLlmApiRoutingChoice.Manual), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[4].Status);
        Assert.Equal(FreeLlmApiStepStatus.Skipped, report.Steps[5].Status);
        Assert.Equal(FreeLlmApiStepStatus.Skipped, report.Steps[6].Status);
        Assert.Equal(3, report.Calls.Count);
    }

    [Fact]
    public async Task The_routing_choice_is_required_before_the_self_test()
    {
        var (setup, _, _) = Build();

        var report = await setup.RunAsync(Acknowledged(["llama-3.3-70b"]), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Ok, report.Steps[4].Status);
        Assert.Equal(FreeLlmApiStepStatus.Skipped, report.Steps[5].Status);
        Assert.Equal(FreeLlmApiStepStatus.Skipped, report.Steps[6].Status);
        Assert.Equal(3, report.Calls.Count);
    }

    [Fact]
    public async Task A_served_model_outside_the_expected_set_is_reported()
    {
        var (setup, handler, _) = Build();
        handler.Route("POST", "/v1/chat/completions", HttpStatusCode.OK, """{"model":"gemini-2.5-flash"}""", new Dictionary<string, string>
        {
            ["X-Routed-Via"] = "google/gemini-2.5-flash",
        });

        var report = await setup.RunAsync(Acknowledged(["llama-3.3-70b"], FreeLlmApiRoutingChoice.Manual), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[6].Status);
        Assert.Equal(["gemini-2.5-flash"], report.Violations);
        Assert.False(report.Completed);
    }

    [Fact]
    public async Task A_trail_model_outside_the_expected_set_is_reported()
    {
        var (setup, handler, _) = Build();
        handler.Route("POST", "/v1/chat/completions", HttpStatusCode.OK, _chat, new Dictionary<string, string>
        {
            ["X-Routed-Via"] = "groq/llama-3.3-70b",
            ["X-Fallback-Trail"] = "groq/llama-3.3-70b;google/gemini-2.5-flash",
        });

        var report = await setup.RunAsync(Acknowledged(["llama-3.3-70b"], FreeLlmApiRoutingChoice.Manual), TestContext.Current.CancellationToken);

        Assert.Equal(["gemini-2.5-flash"], report.Violations);
    }

    [Fact]
    public async Task A_fallback_trail_that_cannot_be_read_is_a_violation()
    {
        var (setup, handler, _) = Build();
        handler.Route("POST", "/v1/chat/completions", HttpStatusCode.OK, _chat, new Dictionary<string, string>
        {
            ["X-Routed-Via"] = "groq/llama-3.3-70b",
            ["X-Fallback-Trail"] = "not a hop",
        });

        var report = await setup.RunAsync(Acknowledged(["llama-3.3-70b"], FreeLlmApiRoutingChoice.Manual), TestContext.Current.CancellationToken);

        Assert.Contains(FreeLlmApiExpectedSetAuditor.MalformedTrailViolation, report.Violations);
        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[6].Status);
    }

    [Fact]
    public async Task A_reply_that_names_no_served_model_is_a_violation()
    {
        var (setup, handler, _) = Build();
        handler.Route("POST", "/v1/chat/completions", HttpStatusCode.OK, "{}");

        var report = await setup.RunAsync(Acknowledged(["llama-3.3-70b"], FreeLlmApiRoutingChoice.Manual), TestContext.Current.CancellationToken);

        Assert.Equal([FreeLlmApiGuidedSetup.ServedModelNotReported], report.Violations);
    }

    [Fact]
    public async Task The_key_never_appears_in_any_result_when_the_run_completes()
    {
        var (setup, handler, _) = Build();

        var report = await setup.RunAsync(
            Acknowledged(["llama-3.3-70b", "gemini-2.5-flash"], FreeLlmApiRoutingChoice.Manual),
            TestContext.Current.CancellationToken);

        Assert.True(report.Completed);
        Assert.All(handler.Sent, s => Assert.Equal($"Bearer {_plantedKey}", s.Authorization));
        AssertNoKey(report);
    }

    [Fact]
    public async Task The_key_never_appears_in_any_result_when_the_upstream_echoes_it_in_an_error()
    {
        var (setup, handler, _) = Build();
        handler.Route("GET", "/v1/usage", HttpStatusCode.Unauthorized, $"{{\"error\":\"bad key {_plantedKey}\"}}");

        var report = await setup.RunAsync(Acknowledged(), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[3].Status);
        AssertNoKey(report);
    }

    [Fact]
    public async Task A_transport_failure_is_reported_as_unreachable_without_the_exception_text()
    {
        var (setup, handler, _) = Build();
        handler.Fail("GET", "/v1/models", new HttpRequestException($"connect failed for {_plantedKey}"));

        var report = await setup.RunAsync(Acknowledged(), TestContext.Current.CancellationToken);

        Assert.Equal(FreeLlmApiStepStatus.Failed, report.Steps[3].Status);
        Assert.Contains("unreachable", report.Steps[3].Message, StringComparison.Ordinal);
        AssertNoKey(report);
    }

    [Theory]
    [InlineData(FreeLlmApiRoutingChoice.Manual, null)]
    [InlineData(FreeLlmApiRoutingChoice.Custom, "custom/*")]
    public void The_routing_choice_maps_to_its_route(FreeLlmApiRoutingChoice choice, string? route) =>
        Assert.Equal(route, FreeLlmApiRoutingChoices.RouteNameFor(choice));

    private static void AssertNoKey(FreeLlmApiSetupReport report) =>
        Assert.All(TextOf(report), text => Assert.DoesNotContain(_plantedKey, text, StringComparison.Ordinal));

    /// <summary>Returns a fixed value for any reference, so a test can see whether the key was read at all.</summary>
    private sealed class FixedResolver(string value) : ISecretResolver
    {
        public bool CanResolve(string reference) => true;

        public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken) => ValueTask.FromResult(value);
    }

    /// <summary>Fails the way an unset variable does.</summary>
    private sealed class ThrowingResolver : ISecretResolver
    {
        public bool CanResolve(string reference) => true;

        public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken) =>
            throw new SecretResolutionException(reference, "variable is not set");
    }

    private sealed record SentCall(string Method, string Path, string? Authorization, string Body);

    /// <summary>A fake handler: routes by method and path, records every request, and never opens a socket.</summary>
    private sealed class SetupHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body, IReadOnlyDictionary<string, string> Headers)> _routes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HttpRequestException> _failures = new(StringComparer.Ordinal);

        public List<SentCall> Sent { get; } = [];

        public void Route(string method, string path, HttpStatusCode status, string body, IReadOnlyDictionary<string, string>? headers = null) =>
            _routes[$"{method} {path}"] = (status, body, headers ?? new Dictionary<string, string>());

        public void Fail(string method, string path, HttpRequestException exception) => _failures[$"{method} {path}"] = exception;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var key = $"{request.Method.Method} {path}";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Sent.Add(new SentCall(request.Method.Method, path, request.Headers.Authorization?.ToString(), body));

            if (_failures.TryGetValue(key, out var failure))
            {
                throw failure;
            }

            if (!_routes.TryGetValue(key, out var route))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
            }

            var response = new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            };
            foreach (var header in route.Headers)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return response;
        }
    }
}
