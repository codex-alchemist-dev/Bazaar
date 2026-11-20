using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.RateLimits;
using Bazaar.Server.Security.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Setup.Http;

/// <summary>
/// The provisioning API (doc 45 section 1.9): <c>POST /setup/plans</c> validates a plan and writes nothing, <c>POST
/// /setup/plans/{hash}/apply</c> applies a plan whose hash matches, and <c>GET /setup/status</c> reports progress. JSON only.
/// Every route runs the same pipeline: transport check (403), the setup rate bucket (429), the access gate (401 or 403), the
/// action, and one audit row. A route answers 503 until its dependencies are registered, and an unexpected error answers 500
/// with one code. Bodies, headers and secrets never reach a log line or an audit row.
/// </summary>
public static class SetupEndpoints
{
    private const string _logCategory = "Bazaar.Server.Setup.Http";
    private const string _jsonMediaType = "application/json";

    /// <summary>Maps the three routes. The gate, the stores and the applier are looked up per request, so a missing one answers 503.</summary>
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder routes)
    {
        // Each route names its method in metadata, so a wrong method answers 405 from the router.
        routes.Map("/setup/plans", Handle(ValidateAsync)).WithMetadata(new HttpMethodMetadata(["POST"]));
        routes.Map("/setup/plans/{hash}/apply", Handle(http => ApplyAsync(http, http.Request.RouteValues["hash"]?.ToString() ?? string.Empty)))
            .WithMetadata(new HttpMethodMetadata(["POST"]));
        routes.Map("/setup/status", Handle(StatusAsync)).WithMetadata(new HttpMethodMetadata(["GET"]));
        return routes;
    }

    private static Task<IResult> ValidateAsync(HttpContext http) => RunAsync(http, "plans.validate", "POST", async call =>
    {
        var (text, refusal) = await ReadPlanAsync(call.Http);
        if (refusal is not null)
        {
            return refusal;
        }

        var loaded = SetupPlanLoader.Load(text!, call.Deps.Settings);
        var status = SetupHttpStatus.For(loaded.ExitCode);
        var body = new SetupPlanReplyBody((int)loaded.ExitCode, loaded.PlanHash, IssuesOf(loaded.Issues));
        return new SetupReply(status, OutcomeOf(loaded.ExitCode), Json(body, status), loaded.PlanHash);
    });

    private static Task<IResult> ApplyAsync(HttpContext http, string hash) => RunAsync(http, "plans.apply", "POST", async call =>
    {
        if (!IsPlanHash(hash))
        {
            return new SetupReply(StatusCodes.Status400BadRequest, "invalid:bad_hash", Error(StatusCodes.Status400BadRequest, "bad_hash"));
        }

        var routeHash = hash.ToLowerInvariant();
        var (text, refusal) = await ReadPlanAsync(call.Http);
        if (refusal is not null)
        {
            return refusal;
        }

        // The plan in the body is checked before anything else: a plan that does not validate has no hash to compare.
        var loaded = SetupPlanLoader.Load(text!, call.Deps.Settings);
        if (loaded.Plan is null || loaded.PlanHash is null)
        {
            var invalid = new SetupPlanReplyBody((int)loaded.ExitCode, null, IssuesOf(loaded.Issues));
            return new SetupReply(SetupHttpStatus.For(loaded.ExitCode), OutcomeOf(loaded.ExitCode), Json(invalid, SetupHttpStatus.For(loaded.ExitCode)));
        }

        if (!string.Equals(loaded.PlanHash, routeHash, StringComparison.Ordinal))
        {
            return new SetupReply(StatusCodes.Status409Conflict, "refused:hash_mismatch", Error(StatusCodes.Status409Conflict, "hash_mismatch"), loaded.PlanHash);
        }

        // A repeat call with the same hash returns the stored result. No second apply runs.
        var stored = await call.Deps.Results.TryGetAsync(loaded.PlanHash, call.Http.RequestAborted);
        if (stored is not null)
        {
            return ApplyReply(stored, replayed: true, "replayed");
        }

        if (!call.Deps.Coordinator.TryBegin(loaded.PlanHash))
        {
            return new SetupReply(StatusCodes.Status409Conflict, "refused:in_progress", Error(StatusCodes.Status409Conflict, "apply_in_progress"), loaded.PlanHash);
        }

        try
        {
            // An apply runs to completion once it starts. A client that disconnects does not cancel it halfway through a step.
            var result = await call.Deps.Applier.ApplyAsync(
                new SetupApplyRequest(loaded.Plan, loaded.PlanHash, SetupEntryPath.Api, call.Decision.Actor),
                CancellationToken.None);

            // A held apply changed nothing, so it is not stored: the same hash may be applied again once the hold is released.
            if (result.ExitCode != SetupExitCode.Held)
            {
                await call.Deps.Results.SaveAsync(loaded.PlanHash, result, CancellationToken.None);
            }

            return ApplyReply(result, replayed: false, OutcomeOf(result.ExitCode));
        }
        finally
        {
            call.Deps.Coordinator.End(loaded.PlanHash);
        }
    });

    private static Task<IResult> StatusAsync(HttpContext http) => RunAsync(http, "status", "GET", async call =>
    {
        var cancellation = call.Http.RequestAborted;
        var progress = await call.Deps.Progress.GetAsync(cancellation);

        // The verification slice is not in this build. When no source is registered the answer is null, not an error.
        var verification = call.Http.RequestServices.GetService<ISetupVerificationSource>();
        var last = verification is null ? null : await verification.GetLastAsync(cancellation);

        var body = new SetupStatusBody(
            progress.Steps.Select(step => new SetupStepBody(step.Id, step.State.ToString().ToLowerInvariant())).ToList(),
            last is null ? null : new SetupVerificationBody(last.Verdict.ToString().ToLowerInvariant(), last.Score, last.CheckedAt),
            progress.HeldApprovals.Select(held => new SetupHeldBody(held.Path, held.Reason)).ToList());
        return new SetupReply(StatusCodes.Status200OK, "ok", Json(body, StatusCodes.Status200OK));
    });

    /// <summary>
    /// The shared pipeline. The order is fixed: dependencies (503), transport (403), the rate bucket (429), the gate (401 or 403),
    /// then the action. One audit row is written for every call, whatever the outcome. A failed audit write answers 500.
    /// </summary>
    private static async Task<IResult> RunAsync(HttpContext http, string route, string method, Func<SetupCall, Task<SetupReply>> action)
    {
        var services = http.RequestServices;
        var deps = SetupDeps.Resolve(services);
        if (deps is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");
        }

        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(_logCategory);
        SetupAccessDecision? decision = null;
        SetupReply reply;
        try
        {
            var caller = await ResolveCallerAsync(http, deps.Auth, http.RequestAborted);
            if (TransportRefused(http, caller.IsLoopback))
            {
                reply = new SetupReply(StatusCodes.Status403Forbidden, "refused:transport", Error(StatusCodes.Status403Forbidden, "forbidden"));
            }
            else
            {
                // The bucket is spent before the gate, so guessing a bootstrap code is bounded by it too.
                var admission = deps.Limiter.TryAcquire(SetupRateLimit.Policy, SetupRateLimit.ScopeKey);
                if (!admission.Allowed)
                {
                    http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(admission.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    reply = new SetupReply(StatusCodes.Status429TooManyRequests, "refused:rate_limited", Error(StatusCodes.Status429TooManyRequests, "rate_limited"));
                }
                else
                {
                    decision = await deps.Gate.EvaluateAsync(caller, http.RequestAborted);
                    reply = decision.Allowed
                        ? await action(new SetupCall(http, deps, decision))
                        : RefusalReply(decision);
                }
            }
        }
        catch (Exception exception)
        {
            if (logger is not null)
            {
                SetupHttpLog.CallFailed(logger, route, exception.GetType().Name);
            }

            reply = new SetupReply(StatusCodes.Status500InternalServerError, "error", Error(StatusCodes.Status500InternalServerError, "unexpected"));
        }

        try
        {
            var actor = decision is { Allowed: true } admitted ? admitted.Actor : SetupHttpConstants.RefusedActor;
            var callerKind = decision is { Allowed: true } ? (decision.Kind == SetupAccessKind.Owner ? "owner" : "bootstrap") : "none";
            var payload = JsonSerializer.Serialize(
                new SetupCallAuditBody(route, method, reply.Status, reply.Outcome, callerKind, reply.PlanHash),
                SetupHttpJsonContext.Default.SetupCallAuditBody);
            await deps.Audit.AppendAsync(new AuditEntry(time.GetUtcNow(), actor, SetupHttpConstants.AuditKind, payload), CancellationToken.None);
        }
        catch (Exception exception)
        {
            if (logger is not null)
            {
                SetupHttpLog.AuditFailed(logger, route, exception.GetType().Name);
            }

            // A call that cannot be audited is not answered as if it had been. The applier's own rows are the record of an apply.
            return Error(StatusCodes.Status500InternalServerError, "unexpected");
        }

        return reply.Result;
    }

    /// <summary>Reads the caller: loopback, the bootstrap code header, and the principal of a valid bearer token, if any.</summary>
    private static async Task<SetupCaller> ResolveCallerAsync(HttpContext http, ISetupCallerAuthenticator auth, CancellationToken cancellationToken)
    {
        var remote = http.Connection.RemoteIpAddress;
        var loopback = remote is not null && IPAddress.IsLoopback(remote);

        var code = http.Request.Headers[SetupHttpConstants.BootstrapCodeHeader].ToString();
        var bootstrapCode = code.Length is 0 or > SetupHttpConstants.MaxBootstrapCodeLength ? null : code;

        SetupPrincipal? principal = null;
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (!string.IsNullOrEmpty(token))
        {
            principal = await auth.AuthenticateAsync(token, cancellationToken);
        }

        return new SetupCaller(loopback, principal, bootstrapCode);
    }

    /// <summary>Plaintext from off the machine is refused unless the operator allowed an overlay, the same rule as the HTTP surface.</summary>
    private static bool TransportRefused(HttpContext http, bool loopback)
    {
        if (http.Request.IsHttps || loopback)
        {
            return false;
        }

        var policy = http.RequestServices.GetService<ISetupTransportPolicy>();
        return policy is null || !policy.AllowPlaintextOverlay;
    }

    /// <summary>A refused call: 403 when a valid credential lacks the role, otherwise 401. Neither says which condition failed.</summary>
    private static SetupReply RefusalReply(SetupAccessDecision decision) =>
        decision.Refusal == SetupRefusal.Forbidden
            ? new SetupReply(StatusCodes.Status403Forbidden, "refused:" + decision.Reason, Error(StatusCodes.Status403Forbidden, "forbidden"))
            : new SetupReply(StatusCodes.Status401Unauthorized, "refused:" + decision.Reason, Error(StatusCodes.Status401Unauthorized, "unauthorized"));

    /// <summary>
    /// Reads the plan body as JSON, at most <see cref="SetupHttpConstants.MaxPlanBytes"/>. A refusal is returned as a reply, so
    /// the caller returns it unchanged.
    /// </summary>
    private static async Task<(string? Text, SetupReply? Refusal)> ReadPlanAsync(HttpContext http)
    {
        var contentType = http.Request.ContentType ?? string.Empty;
        if (!contentType.StartsWith(_jsonMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return (null, new SetupReply(StatusCodes.Status415UnsupportedMediaType, "invalid:media_type", Error(StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type")));
        }

        if (http.Request.ContentLength is > SetupHttpConstants.MaxPlanBytes)
        {
            return (null, new SetupReply(StatusCodes.Status413PayloadTooLarge, "invalid:too_large", Error(StatusCodes.Status413PayloadTooLarge, "too_large")));
        }

        var bytes = await ReadBoundedAsync(http.Request.Body, SetupHttpConstants.MaxPlanBytes, http.RequestAborted);
        if (bytes is null)
        {
            return (null, new SetupReply(StatusCodes.Status413PayloadTooLarge, "invalid:too_large", Error(StatusCodes.Status413PayloadTooLarge, "too_large")));
        }

        return (Encoding.UTF8.GetString(bytes), null);
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes. Returns null when the body is longer, without reading past the limit.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static SetupReply ApplyReply(SetupApplyResult result, bool replayed, string outcome)
    {
        var status = SetupHttpStatus.For(result.ExitCode);
        var body = new SetupApplyReplyBody((int)result.ExitCode, result.PlanHash, result.AppliedStepIds, IssuesOf(result.Issues), replayed);
        return new SetupReply(status, outcome, Json(body, status), result.PlanHash);
    }

    private static List<SetupIssueBody> IssuesOf(IEnumerable<SetupValidationIssue> issues) =>
        issues.Select(issue => new SetupIssueBody(issue.Severity.ToString().ToLowerInvariant(), issue.Path, issue.Message)).ToList();

    private static string OutcomeOf(SetupExitCode code) => code switch
    {
        SetupExitCode.Ok => "ok",
        SetupExitCode.InvalidInput => "invalid",
        SetupExitCode.Held => "held",
        SetupExitCode.ApplyFailedRolledBack => "failed",
        _ => "failed",
    };

    /// <summary>A plan hash is 64 hexadecimal characters, in either case.</summary>
    private static bool IsPlanHash(string value) =>
        value.Length == SetupHttpConstants.PlanHashLength && value.All(Uri.IsHexDigit);

    // One overload per body type, so every response is written with its source-generated type info (AOT-safe, no reflection).
    private static IResult Json(SetupPlanReplyBody body, int status) =>
        Results.Json(body, SetupHttpJsonContext.Default.SetupPlanReplyBody, _jsonMediaType, status);

    private static IResult Json(SetupApplyReplyBody body, int status) =>
        Results.Json(body, SetupHttpJsonContext.Default.SetupApplyReplyBody, _jsonMediaType, status);

    private static IResult Json(SetupStatusBody body, int status) =>
        Results.Json(body, SetupHttpJsonContext.Default.SetupStatusBody, _jsonMediaType, status);

    private static IResult Error(int status, string code) =>
        Results.Json(new SetupErrorBody(code), SetupHttpJsonContext.Default.SetupErrorBody, _jsonMediaType, status);

    /// <summary>Wraps a route so it runs without the reflection-based minimal API binder, which the AOT analyzers reject.</summary>
    private static RequestDelegate Handle(Func<HttpContext, Task<IResult>> route) => async http =>
    {
        var result = await route(http);
        await result.ExecuteAsync(http);
    };

    /// <summary>What the route resolved from DI. Any missing entry means the route answers 503 and writes nothing.</summary>
    private sealed record SetupDeps(
        ISetupAccessGate Gate,
        SetupApplier Applier,
        IApplyResultStore Results,
        ISetupProgressSource Progress,
        IRateLimiter Limiter,
        IAuditLog Audit,
        ISetupCallerAuthenticator Auth,
        SetupApplyCoordinator Coordinator,
        ISetupSettingCatalog Settings)
    {
        public static SetupDeps? Resolve(IServiceProvider services)
        {
            var gate = services.GetService<ISetupAccessGate>();
            var applier = services.GetService<SetupApplier>();
            var results = services.GetService<IApplyResultStore>();
            var progress = services.GetService<ISetupProgressSource>();
            var limiter = services.GetService<IRateLimiter>();
            var audit = services.GetService<IAuditLog>();
            var auth = services.GetService<ISetupCallerAuthenticator>();
            var coordinator = services.GetService<SetupApplyCoordinator>();
            var settings = services.GetService<ISetupSettingCatalog>();
            if (gate is null || applier is null || results is null || progress is null || limiter is null || audit is null || auth is null || coordinator is null || settings is null)
            {
                return null;
            }

            return new SetupDeps(gate, applier, results, progress, limiter, audit, auth, coordinator, settings);
        }
    }

    private sealed record SetupCall(HttpContext Http, SetupDeps Deps, SetupAccessDecision Decision);

    /// <summary>One answer: its status, the outcome for the audit row, the response, and the plan hash when there is one.</summary>
    private sealed record SetupReply(int Status, string Outcome, IResult Result, string? PlanHash = null);
}

/// <summary>Log lines for the provisioning API. They carry the route and the exception type only, never a body, header or message.</summary>
internal static partial class SetupHttpLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Setup API call on {Route} failed with {ExceptionType}.")]
    public static partial void CallFailed(ILogger logger, string route, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Setup API audit write for {Route} failed with {ExceptionType}.")]
    public static partial void AuditFailed(ILogger logger, string route, string exceptionType);
}
