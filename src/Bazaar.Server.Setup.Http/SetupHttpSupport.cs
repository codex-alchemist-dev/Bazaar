using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.RateLimits;
using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.Setup.Http;

/// <summary>
/// The one place that maps setup exit codes to HTTP status codes (doc 45 section 1.7): 0 is 200, 1 is 400, 2 is 422, and 3
/// (applied steps rolled back) is 500. A 409 is used only for a hash that does not match the plan, or an apply already running.
/// </summary>
public static class SetupHttpStatus
{
    public static int For(SetupExitCode code) => code switch
    {
        SetupExitCode.Ok => StatusCodes.Status200OK,
        SetupExitCode.InvalidInput => StatusCodes.Status400BadRequest,
        SetupExitCode.Held => StatusCodes.Status422UnprocessableEntity,
        SetupExitCode.ApplyFailedRolledBack => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status500InternalServerError,
    };
}

/// <summary>
/// The rate bucket of the provisioning API. It is its own named policy, so no other route spends it. The bucket is installation
/// wide, which also bounds how fast a bootstrap code can be guessed.
/// </summary>
public static class SetupRateLimit
{
    /// <summary>Thirty calls a minute across the setup routes. WORKING DEFAULT; the settings key is not registered yet.</summary>
    public static readonly RateLimitPolicy Policy = new(
        "setup_api", 30, TimeSpan.FromMinutes(1), RateLimitScope.Installation, "security.quota.setup_api");

    /// <summary>The scope key. One bucket for the whole install.</summary>
    public const string ScopeKey = "install";
}

/// <summary>
/// Admits one apply per plan hash at a time, in this process. A second call for a hash already applying is refused with 409,
/// so the applier never runs twice at once for one plan. The instance lock is the cross-process guard.
/// </summary>
public sealed class SetupApplyCoordinator
{
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Takes the hash. False means an apply for it is already running.</summary>
    public bool TryBegin(string planHash)
    {
        lock (_lock)
        {
            return _inFlight.Add(planHash);
        }
    }

    /// <summary>Releases the hash once its apply has finished, whatever the outcome.</summary>
    public void End(string planHash)
    {
        lock (_lock)
        {
            _inFlight.Remove(planHash);
        }
    }
}

/// <summary>The body of an error answer: one machine code, never a value or a path.</summary>
public sealed record SetupErrorBody(string Error);

/// <summary>One validation issue as the API shows it: a severity, the key path, and the rule. Never a value.</summary>
public sealed record SetupIssueBody(string Severity, string Path, string Message);

/// <summary>The answer of <c>POST /setup/plans</c>.</summary>
public sealed record SetupPlanReplyBody(int ExitCode, string? PlanHash, IReadOnlyList<SetupIssueBody> Issues);

/// <summary>The answer of <c>POST /setup/plans/{hash}/apply</c>. <see cref="Replayed"/> is true when the stored result was returned.</summary>
public sealed record SetupApplyReplyBody(
    int ExitCode,
    string PlanHash,
    IReadOnlyList<string> AppliedSteps,
    IReadOnlyList<SetupIssueBody> Issues,
    bool Replayed);

/// <summary>One step in the status answer.</summary>
public sealed record SetupStepBody(string Id, string State);

/// <summary>One held approval in the status answer.</summary>
public sealed record SetupHeldBody(string Path, string Reason);

/// <summary>The last verification in the status answer.</summary>
public sealed record SetupVerificationBody(string Verdict, int Score, DateTimeOffset CheckedAt);

/// <summary>The answer of <c>GET /setup/status</c>.</summary>
public sealed record SetupStatusBody(
    IReadOnlyList<SetupStepBody> Steps,
    SetupVerificationBody? LastVerification,
    IReadOnlyList<SetupHeldBody> HeldApprovals);

/// <summary>The audit payload of one call. It holds the route, the outcome and the caller kind, never a body or a secret.</summary>
public sealed record SetupCallAuditBody(string Route, string Method, int Status, string Outcome, string Caller, string? PlanHash);

/// <summary>Source-generated JSON for the provisioning API (CLAUDE.md: System.Text.Json source generation, no reflection).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(SetupErrorBody))]
[JsonSerializable(typeof(SetupPlanReplyBody))]
[JsonSerializable(typeof(SetupApplyReplyBody))]
[JsonSerializable(typeof(SetupStatusBody))]
[JsonSerializable(typeof(SetupCallAuditBody))]
internal sealed partial class SetupHttpJsonContext : JsonSerializerContext;
