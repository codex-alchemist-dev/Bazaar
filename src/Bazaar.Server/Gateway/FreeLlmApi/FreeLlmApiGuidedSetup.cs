using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// FreeLLMAPI guided setup (docs/design/05-model-gateway.md, "FreeLLMAPI guided setup", steps 1 to 7), run as data for the
/// admin panel. <see cref="RunAsync"/> goes through the steps in order and stops at the first step that fails or waits for
/// operator input; the steps after it are reported as skipped. The only state kept is the warning acknowledgements. Candidate
/// models, the preset and the routing choice are returned in the report, not stored.
/// <para>
/// The key is resolved through <see cref="ISecretResolver"/> once a run reaches step 4. No step result, probe call or
/// exception message carries it: messages are fixed text, and URLs never include the base URL's userinfo.
/// </para>
/// <para>
/// TODO(component): the admin panel binding and the durable acknowledgement store wait for the component model (PR #125, not
/// merged). No registration is added here; the host composes this class when that work lands.
/// </para>
/// </summary>
/// <param name="http">Must have its <see cref="HttpClient.Timeout"/> set by the caller.</param>
/// <param name="secrets">Resolves the key reference (<c>env:</c> or <c>file:</c>) for the probes.</param>
/// <param name="acknowledgements">Stores the step 2 acknowledgements with their timestamps.</param>
/// <param name="time">The clock for acknowledgement timestamps. Defaults to the system clock.</param>
public sealed class FreeLlmApiGuidedSetup(
    HttpClient http,
    ISecretResolver secrets,
    IWarningAcknowledgementStore acknowledgements,
    TimeProvider? time = null)
{
    /// <summary>The default base URL from the design doc (step 3).</summary>
    public const string DefaultBaseUrl = "http://localhost:3001/v1";

    internal const string ConnectMessage = "Connect existing: install docs are linked. The curl installer is not run.";

    /// <summary>Violation text when a reply names no served model and carries no routing header.</summary>
    public const string ServedModelNotReported = "(served model not reported)";

    private const string _probeMessage = "Reply with OK.";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly ISecretResolver _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    private readonly IWarningAcknowledgementStore _acknowledgements = acknowledgements ?? throw new ArgumentNullException(nameof(acknowledgements));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Runs guided setup once, from step 1 to the first step that stops it.</summary>
    public async Task<FreeLlmApiSetupReport> RunAsync(FreeLlmApiSetupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var steps = new List<FreeLlmApiStepResult>
        {
            new(FreeLlmApiSetupStep.Connect, FreeLlmApiStepStatus.Ok, ConnectMessage),
        };
        var calls = new List<FreeLlmApiProbeCall>();

        // Step 2: every warning must be acknowledged before anything else runs. No network call happens before this.
        var warnings = await AcknowledgeAsync(request.AcknowledgedWarningIds, ct);
        steps.Add(warnings);
        if (warnings.Status != FreeLlmApiStepStatus.Ok)
        {
            return Stopped(steps, calls, [], []);
        }

        // Step 3: the base URL and a secret reference. A literal key is refused here and never echoed.
        var (endpoint, baseUrl) = ValidateEndpoint(request.BaseUrl, request.KeyReference);
        steps.Add(endpoint);
        if (endpoint.Status != FreeLlmApiStepStatus.Ok)
        {
            return Stopped(steps, calls, [], []);
        }

        // Step 4: models, usage, then one test message. The first failure stops the run, so later calls are not sent.
        var key = await ResolveKeyAsync(request.KeyReference!, ct);
        if (key is null)
        {
            steps.Add(new(FreeLlmApiSetupStep.Probe, FreeLlmApiStepStatus.Failed, "The key reference could not be resolved on this host."));
            return Stopped(steps, calls, [], []);
        }

        var probe = await ProbeAsync(baseUrl, key, calls, ct);
        steps.Add(probe.Result);
        if (probe.Result.Status != FreeLlmApiStepStatus.Ok)
        {
            return Stopped(steps, calls, probe.Candidates, []);
        }

        // Step 5: the operator ticks models from the candidate list. Nothing is ticked yet means wait for input.
        var preset = SelectPreset(request.PresetModelIds, probe.Candidates);
        steps.Add(preset.Result);
        if (preset.Result.Status != FreeLlmApiStepStatus.Ok)
        {
            return Stopped(steps, calls, probe.Candidates, []);
        }

        // Step 6: the routing choice. Null means the operator has not chosen yet.
        if (request.Routing is not { } routing)
        {
            steps.Add(new(FreeLlmApiSetupStep.Routing, FreeLlmApiStepStatus.Skipped, "Awaiting routing choice: Manual or Custom."));
            return Stopped(steps, calls, probe.Candidates, []);
        }

        var routeName = FreeLlmApiRoutingChoices.RouteNameFor(routing);
        steps.Add(new(
            FreeLlmApiSetupStep.Routing,
            FreeLlmApiStepStatus.Ok,
            routeName is null
                ? "Manual routing: Bazaar pins model ids. No dynamic route is created."
                : $"Custom routing: routes through {routeName}."));

        // Step 7: one test message pinned to the first ticked model; the served model must be inside the ticked set.
        var selfTest = await SelfTestAsync(baseUrl, key, preset.Models, calls, ct);
        steps.Add(selfTest.Result);
        return new FreeLlmApiSetupReport(steps, calls, probe.Candidates, selfTest.Violations);
    }

    private async Task<FreeLlmApiStepResult> AcknowledgeAsync(IReadOnlyList<string>? ticked, CancellationToken ct)
    {
        var known = new HashSet<string>(FreeLlmApiWarnings.All.Select(w => w.Id), StringComparer.Ordinal);
        var now = _time.GetUtcNow();

        var unknown = 0;
        foreach (var id in ticked ?? [])
        {
            if (!known.Contains(id))
            {
                unknown++;
                continue;
            }

            await _acknowledgements.RecordAsync(id, now, ct);
        }

        if (unknown > 0)
        {
            return new(FreeLlmApiSetupStep.Warnings, FreeLlmApiStepStatus.Failed, $"{unknown} unknown warning id(s) were ticked. Nothing was recorded for them.");
        }

        var missing = 0;
        foreach (var warning in FreeLlmApiWarnings.All)
        {
            if (await _acknowledgements.GetAsync(warning.Id, ct) is null)
            {
                missing++;
            }
        }

        return missing == 0
            ? new(FreeLlmApiSetupStep.Warnings, FreeLlmApiStepStatus.Ok, $"All {FreeLlmApiWarnings.All.Count} warnings are acknowledged.")
            : new(FreeLlmApiSetupStep.Warnings, FreeLlmApiStepStatus.Failed, $"{missing} of {FreeLlmApiWarnings.All.Count} warnings are not acknowledged. Setup stops until all are.");
    }

    /// <summary>Step 3. Returns the result and the normalised base URL (used only when the step is Ok).</summary>
    private static (FreeLlmApiStepResult Result, string BaseUrl) ValidateEndpoint(string? rawBaseUrl, string? keyReference)
    {
        var baseUrl = string.IsNullOrWhiteSpace(rawBaseUrl) ? DefaultBaseUrl : rawBaseUrl.Trim().TrimEnd('/');

        // The text of the base URL is never echoed: it may carry credentials that a mistaken operator pasted.
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return (new(FreeLlmApiSetupStep.Endpoint, FreeLlmApiStepStatus.Failed,
                "The base URL must be an absolute http or https URL with no credentials, query or fragment."), baseUrl);
        }

        if (string.IsNullOrWhiteSpace(keyReference))
        {
            return (new(FreeLlmApiSetupStep.Endpoint, FreeLlmApiStepStatus.Failed,
                "The key is required as a secret reference (env:NAME or file:PATH)."), baseUrl);
        }

        // A literal key is refused and never shown.
        if (!FreeLlmApiOptionsValidator.IsSecretReference(keyReference.Trim()))
        {
            return (new(FreeLlmApiSetupStep.Endpoint, FreeLlmApiStepStatus.Failed,
                "The key must be a secret reference (env:NAME or file:PATH). A literal key is refused and not shown."), baseUrl);
        }

        return (new(FreeLlmApiSetupStep.Endpoint, FreeLlmApiStepStatus.Ok,
            $"Base URL {baseUrl} and a secret reference are valid."), baseUrl);
    }

    private async Task<string?> ResolveKeyAsync(string keyReference, CancellationToken ct)
    {
        try
        {
            var key = await _secrets.ResolveAsync(keyReference.Trim(), ct);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (SecretResolutionException)
        {
            // The exception names the reference and its reason. The reference is not echoed here, to keep the report fixed-form.
            return null;
        }
    }

    private sealed record ProbeOutcome(FreeLlmApiStepResult Result, IReadOnlyList<FreeLlmApiModel> Candidates);

    /// <summary>Step 4: GET models, GET usage, then POST one test message to the first ready model.</summary>
    private async Task<ProbeOutcome> ProbeAsync(string baseUrl, string key, List<FreeLlmApiProbeCall> calls, CancellationToken ct)
    {
        var models = await SendAsync(HttpMethod.Get, $"{baseUrl}/models", key, body: null, calls, ct);
        if (!models.Succeeded)
        {
            return Fail(models, "GET", $"{baseUrl}/models");
        }

        if (!FreeLlmApiModelCatalog.TryParse(models.Body!, out var candidates))
        {
            return new(new(FreeLlmApiSetupStep.Probe, FreeLlmApiStepStatus.Failed, $"GET {baseUrl}/models returned a malformed model list."), []);
        }

        var ready = candidates.FirstOrDefault(m => m.ExecutionStatus is null || m.ExecutionStatus == FreeLlmApiModel.ReadyStatus);
        if (ready is null)
        {
            return new(new(FreeLlmApiSetupStep.Probe, FreeLlmApiStepStatus.Failed, $"GET {baseUrl}/models lists no ready models."), candidates);
        }

        var usage = await SendAsync(HttpMethod.Get, $"{baseUrl}/usage", key, body: null, calls, ct);
        if (!usage.Succeeded)
        {
            return new(Fail(usage, "GET", $"{baseUrl}/usage").Result, candidates);
        }

        if (FreeLlmApiUsageParser.TryParse(usage.Body!) is null)
        {
            return new(new(FreeLlmApiSetupStep.Probe, FreeLlmApiStepStatus.Failed, $"GET {baseUrl}/usage returned a malformed usage report."), candidates);
        }

        var test = await SendAsync(HttpMethod.Post, $"{baseUrl}/chat/completions", key, ChatBody(ready.Id), calls, ct);
        if (!test.Succeeded)
        {
            return new(Fail(test, "POST", $"{baseUrl}/chat/completions").Result, candidates);
        }

        return new(new(FreeLlmApiSetupStep.Probe, FreeLlmApiStepStatus.Ok,
            $"GET /models, GET /usage and the test message all succeeded. {candidates.Count} model(s) listed."), candidates);
    }

    private sealed record PresetOutcome(FreeLlmApiStepResult Result, IReadOnlyList<string> Models);

    /// <summary>Step 5: the ticked models must all be listed and ready. Nothing ticked is a wait, not a failure.</summary>
    private static PresetOutcome SelectPreset(IReadOnlyList<string>? ticked, IReadOnlyList<FreeLlmApiModel> candidates)
    {
        var distinct = (ticked ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 0)
        {
            return new(new(FreeLlmApiSetupStep.Preset, FreeLlmApiStepStatus.Skipped,
                $"Awaiting the free-lane preset: tick models from the {candidates.Count} candidate(s)."), []);
        }

        // Ids are not echoed in the failure text, so a pasted key in this list cannot reach the report.
        var notListed = distinct.Count(id => !candidates.Any(c => string.Equals(c.Id, id, StringComparison.Ordinal)));
        if (notListed > 0)
        {
            return new(new(FreeLlmApiSetupStep.Preset, FreeLlmApiStepStatus.Failed,
                $"{notListed} ticked model id(s) are not in the FreeLLMAPI model list. Tick only listed models."), []);
        }

        var notReady = distinct.Count(id => candidates.Any(c =>
            string.Equals(c.Id, id, StringComparison.Ordinal)
            && c.ExecutionStatus is not null
            && c.ExecutionStatus != FreeLlmApiModel.ReadyStatus));
        if (notReady > 0)
        {
            return new(new(FreeLlmApiSetupStep.Preset, FreeLlmApiStepStatus.Failed,
                $"{notReady} ticked model(s) are listed but not ready. Untick them or fix the upstream key."), []);
        }

        // Tool-capability filtering is not in the parsed model list (FreeLlmApiModel carries no capabilities). The operator's
        // tick is the only filter for now.
        return new(new(FreeLlmApiSetupStep.Preset, FreeLlmApiStepStatus.Ok,
            $"Free-lane preset: {distinct.Count} model(s) ticked."), distinct);
    }

    /// <summary>
    /// Step 7: one test message pinned to the first ticked model. Violations are served models outside the ticked set, trail
    /// models outside it, a trail that cannot be read in full, or a served model that is not reported at all.
    /// </summary>
    private async Task<(FreeLlmApiStepResult Result, IReadOnlyList<string> Violations)> SelfTestAsync(
        string baseUrl,
        string key,
        IReadOnlyList<string> expected,
        List<FreeLlmApiProbeCall> calls,
        CancellationToken ct)
    {
        var url = $"{baseUrl}/chat/completions";
        var reply = await SendAsync(HttpMethod.Post, url, key, ChatBody(expected[0]), calls, ct);
        if (!reply.Succeeded)
        {
            return (new(FreeLlmApiSetupStep.SelfTest, FreeLlmApiStepStatus.Failed, $"POST {url} failed: {reply.Detail}."), []);
        }

        var routing = FreeLlmApiRouting.Parse(reply.Headers!);
        var served = routing.RoutedModel ?? ServedModelOf(reply.Body);

        var violations = new List<string>();
        if (served is null)
        {
            violations.Add(ServedModelNotReported);
        }
        else if (!expected.Contains(served, StringComparer.Ordinal))
        {
            violations.Add(served);
        }

        foreach (var trailModel in routing.TrailModels.Where(m => !expected.Contains(m, StringComparer.Ordinal)))
        {
            violations.Add(trailModel);
        }

        if (routing.TrailMalformed)
        {
            violations.Add(FreeLlmApiExpectedSetAuditor.MalformedTrailViolation);
        }

        var distinct = violations.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 0)
        {
            return (new(FreeLlmApiSetupStep.SelfTest, FreeLlmApiStepStatus.Failed,
                $"{distinct.Count} expected-set violation(s) on the test message."), distinct);
        }

        return (new(FreeLlmApiSetupStep.SelfTest, FreeLlmApiStepStatus.Ok,
            $"The test message was served by {served} inside the {expected.Count}-model preset."), distinct);
    }

    private async Task<HttpOutcome> SendAsync(
        HttpMethod method,
        string url,
        string key,
        string? body,
        List<FreeLlmApiProbeCall> calls,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Absolute));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var succeeded = response.IsSuccessStatusCode;
            calls.Add(new(method.Method, url, status, succeeded));
            if (!succeeded)
            {
                return new HttpOutcome(false, status, null, null, $"HTTP {status}");
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new HttpOutcome(true, status, text, response.Headers, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            calls.Add(new(method.Method, url, null, false));
            return new HttpOutcome(false, null, null, null, "timeout");
        }
        catch (HttpRequestException)
        {
            calls.Add(new(method.Method, url, null, false));
            return new HttpOutcome(false, null, null, null, "unreachable");
        }
    }

    private sealed record HttpOutcome(bool Succeeded, int? Status, string? Body, HttpResponseHeaders? Headers, string? Detail);

    private static ProbeOutcome Fail(HttpOutcome outcome, string method, string url) =>
        new(new(FreeLlmApiSetupStep.Probe, FreeLlmApiStepStatus.Failed, $"{method} {url} failed: {outcome.Detail}."), []);

    private static string ChatBody(string model)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", _probeMessage);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteNumber("max_tokens", 8);
            writer.WriteBoolean("stream", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The <c>model</c> field of a chat reply, or null when absent or unreadable.</summary>
    private static string? ServedModelOf(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(model.GetString())
                    ? model.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Marks every step after the last one that ran as skipped, so the report always holds all seven steps.</summary>
    private static FreeLlmApiSetupReport Stopped(
        List<FreeLlmApiStepResult> steps,
        List<FreeLlmApiProbeCall> calls,
        IReadOnlyList<FreeLlmApiModel> candidates,
        IReadOnlyList<string> violations)
    {
        var last = steps[^1].Step;
        for (var next = (int)last + 1; next <= (int)FreeLlmApiSetupStep.SelfTest; next++)
        {
            steps.Add(new((FreeLlmApiSetupStep)next, FreeLlmApiStepStatus.Skipped, $"Not run: step {(int)last} did not complete."));
        }

        return new FreeLlmApiSetupReport(steps, calls, candidates, violations);
    }
}
