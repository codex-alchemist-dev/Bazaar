using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Bazaar.Client.CompanySetup;

/// <summary>
/// The provisioning API over HTTP (doc 45 section 1.9). The caller builds the <see cref="HttpClient"/> from the server endpoint, so
/// the key-hash pin and the loopback rule already apply. A bootstrap code goes in <c>X-Bazaar-Bootstrap-Code</c> and a token in the
/// bearer header; neither is logged or echoed.
/// </summary>
public sealed class HttpCompanySetupService(HttpClient http) : ICompanySetupService
{
    /// <summary>The header the server reads the one-time bootstrap code from (<c>SetupHttpConstants.BootstrapCodeHeader</c>).</summary>
    public const string BootstrapCodeHeader = "X-Bazaar-Bootstrap-Code";

    private const string _refused = "The server did not accept that setup code. Check the code the server console printed.";
    private const string _unavailable = "The server could not take the setup request right now. Wait a moment and try again.";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <inheritdoc />
    public async Task<SetupValidation> ValidateAsync(CompanySetupRequest request, SetupAccess access, CancellationToken cancellationToken = default)
    {
        try
        {
            using var reply = await SendAsync("setup/plans", request, access, cancellationToken);
            if (Refusal(reply.StatusCode) is { } refusal) return new SetupValidation(refusal, null, [], MessageFor(refusal));

            var body = await ReadAsync(reply, SetupJsonContext.Default.PlanReplyBody, cancellationToken);
            if (body is null) return new SetupValidation(SetupCallOutcome.Unavailable, null, [], _unavailable);

            var outcome = OutcomeOf(body.ExitCode);
            var issues = IssuesOf(body.Issues);
            return new SetupValidation(outcome, outcome is SetupCallOutcome.Ok or SetupCallOutcome.Held ? body.PlanHash : null, issues, SummaryOf(outcome, issues));
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            return new SetupValidation(SetupCallOutcome.Unavailable, null, [], _unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<SetupApplication> ApplyAsync(CompanySetupRequest request, string planHash, SetupAccess access, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);
        try
        {
            using var reply = await SendAsync("setup/plans/" + Uri.EscapeDataString(planHash) + "/apply", request, access, cancellationToken);
            if (Refusal(reply.StatusCode) is { } refusal) return new SetupApplication(refusal, [], [], false, MessageFor(refusal));
            if (reply.StatusCode == HttpStatusCode.Conflict) return new SetupApplication(SetupCallOutcome.Unavailable, [], [], false, "Setup is already running, or the plan changed. Check the plan again.");

            var body = await ReadAsync(reply, SetupJsonContext.Default.ApplyReplyBody, cancellationToken);
            if (body is null) return new SetupApplication(SetupCallOutcome.Unavailable, [], [], false, _unavailable);

            var outcome = OutcomeOf(body.ExitCode);
            var issues = IssuesOf(body.Issues);
            return new SetupApplication(outcome, body.AppliedSteps ?? [], issues, body.Replayed, SummaryOf(outcome, issues));
        }
        catch (Exception exception) when (IsTransport(exception, cancellationToken))
        {
            return new SetupApplication(SetupCallOutcome.Unavailable, [], [], false, _unavailable);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string route, CompanySetupRequest request, SetupAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);

        using var message = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(SetupPlanText.Build(request), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(access.BootstrapCode)) message.Headers.TryAddWithoutValidation(BootstrapCodeHeader, access.BootstrapCode.Trim());
        if (!string.IsNullOrWhiteSpace(access.BearerToken)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.BearerToken);
        return await _http.SendAsync(message, cancellationToken);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage reply, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        var text = await reply.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(text, typeInfo);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static SetupCallOutcome? Refusal(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SetupCallOutcome.AccessRefused,
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError => SetupCallOutcome.Unavailable,
        _ => null,
    };

    private static SetupCallOutcome OutcomeOf(int exitCode) => exitCode switch
    {
        0 => SetupCallOutcome.Ok,
        1 => SetupCallOutcome.Invalid,
        2 => SetupCallOutcome.Held,
        3 => SetupCallOutcome.ApplyFailed,
        _ => SetupCallOutcome.Unavailable,
    };

    private static string MessageFor(SetupCallOutcome outcome) => outcome == SetupCallOutcome.AccessRefused ? _refused : _unavailable;

    private static string SummaryOf(SetupCallOutcome outcome, IReadOnlyList<SetupIssue> issues) => outcome switch
    {
        SetupCallOutcome.Ok => "The plan is valid.",
        SetupCallOutcome.Invalid => $"The plan has {issues.Count(i => !string.Equals(i.Severity, "hold", StringComparison.OrdinalIgnoreCase))} problem(s). Fix them and check again.",
        SetupCallOutcome.Held => "Part of this setup needs the owner's approval before it can run. Nothing has changed.",
        SetupCallOutcome.ApplyFailed => "A setup step failed and was undone. Nothing is half-applied.",
        _ => _unavailable,
    };

    private static List<SetupIssue> IssuesOf(List<IssueBody>? issues) =>
        [.. (issues ?? []).Select(i => new SetupIssue(i.Severity ?? "error", i.Path ?? "", i.Message ?? ""))];

    private static bool IsTransport(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested;
}
