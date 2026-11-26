using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Agents.Turns.Tools;

/// <summary>
/// Runs one tool call for an agent turn (doc 06 "Tool dispatch", doc 15). Order: reader check, registry lookup,
/// argument parse, in-flight check, policy decision, execution, then exactly one audit record for the call.
/// </summary>
/// <remarks>
/// The taint gate is not repeated here. <see cref="IToolPolicyEngine"/> owns the Tier 2 hard-taint gate, so a second
/// copy in the dispatcher could only drift from it.
/// <para>
/// Tool failures: a tool that throws (other than cancellation) is not a policy outcome. The call returns
/// <see cref="ToolDispatchOutcome.Executed"/> with the fixed result <c>{"error":"tool failed"}</c>, and the audit
/// record reason is <c>tool_exception:&lt;type name&gt;</c>. The exception message is kept out of both the result
/// and the log, because it can carry host paths or secrets. Cancellation is rethrown after an audit record with
/// outcome Unknown, and the call stays in flight, so a non-idempotent retry reports Unknown.
/// </para>
/// <para>
/// Grants: the dispatch context carries no verified grant yet. The approval lookup belongs to the approval flow
/// (<c>ApprovalService.VerifyGrantAsync</c>) and is wired in a later package. Until then a Tier 2 call returns
/// PendingApproval and never runs from this path.
/// </para>
/// </remarks>
public sealed class ToolDispatcher(
    IToolRegistry tools,
    IToolPolicyEngine policy,
    ISessionTaintStore sessionTaint,
    IInFlightCallStore inFlight,
    IAuditLog auditLog,
    TimeProvider time,
    ToolDispatcherOptions? options = null) : IToolDispatcher
{
    private const string _readerTurnReason = "reader turn";
    private const string _unknownToolReason = "unknown tool";
    private const string _malformedArgumentsReason = "malformed arguments";
    private const string _inFlightReason = "in flight at crash";
    private const string _unknownPolicyOutcomeReason = "unknown policy outcome";
    private const string _cancelledReason = "cancelled";
    private const string _toolFailedResult = """{"error":"tool failed"}""";
    private const string _auditKind = "tool.dispatch";

    private readonly IToolRegistry _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    private readonly IToolPolicyEngine _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly ISessionTaintStore _sessionTaint = sessionTaint ?? throw new ArgumentNullException(nameof(sessionTaint));
    private readonly IInFlightCallStore _inFlight = inFlight ?? throw new ArgumentNullException(nameof(inFlight));
    private readonly IAuditLog _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ToolDispatcherOptions _options = options ?? ToolDispatcherOptions.Default;

    /// <inheritdoc />
    public async Task<ToolDispatchResult> InvokeAsync(ToolDispatchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Call);

        var start = _time.GetTimestamp();
        var call = request.Call;
        var argsHash = HashArguments(call.ArgumentsJson);

        if (request.Role == TurnRole.Reader && !_options.ReaderReadOnlyTools.Contains(call.Name))
        {
            return await StopAsync(request, argsHash, start, ToolDispatchOutcome.Denied, _readerTurnReason, cancellationToken);
        }

        if (!_tools.TryGet(call.Name, out var implementation))
        {
            return await StopAsync(request, argsHash, start, ToolDispatchOutcome.Denied, _unknownToolReason, cancellationToken);
        }

        if (!TryParseArguments(call.ArgumentsJson, out var args))
        {
            return await StopAsync(request, argsHash, start, ToolDispatchOutcome.Denied, _malformedArgumentsReason, cancellationToken);
        }

        // A call begun before a crash and never completed may have run. Only an idempotent call is allowed to run again.
        var callKey = $"{request.TurnId:D}:{call.Id}";
        if (!request.Idempotent && await _inFlight.HasUnfinishedAsync(callKey, cancellationToken))
        {
            return await StopAsync(request, argsHash, start, ToolDispatchOutcome.Unknown, _inFlightReason, cancellationToken);
        }

        var taint = await _sessionTaint.LoadAsync(request.SessionId, cancellationToken);
        var decision = _policy.Evaluate(new DispatchContext(
            AgentId: request.AgentId.ToString("D"),
            SessionId: request.SessionId,
            Role: ToSessionRoles(request.Role),
            Tool: call.Name,
            Platform: null,
            Target: null,
            Args: args,
            Taint: taint,
            Now: _time.GetUtcNow()));

        switch (decision.Outcome)
        {
            case PolicyOutcome.Allow:
                break;

            case PolicyOutcome.RequireApproval:
                return await StopAsync(request, argsHash, start, ToolDispatchOutcome.PendingApproval, decision.Reason.ToString(), cancellationToken);

            case PolicyOutcome.Deny:
                return await StopAsync(request, argsHash, start, ToolDispatchOutcome.Denied, decision.Reason.ToString(), cancellationToken);

            default:
                // Fail closed on an outcome this dispatcher does not know.
                return await StopAsync(request, argsHash, start, ToolDispatchOutcome.Denied, _unknownPolicyOutcomeReason, cancellationToken);
        }

        await _inFlight.BeginAsync(callKey, cancellationToken);
        string resultJson;
        string? failure = null;
        try
        {
            // A contextual tool gets the caller the dispatcher already verified (doc 15). Other tools keep the plain call.
            resultJson = implementation is IContextualTool contextual
                ? await contextual.ExecuteAsync(args, new ToolCallContext(request.AgentId, request.SessionId, request.TurnId, request.ChainDepth), cancellationToken)
                : await implementation.ExecuteAsync(args, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Whether the tool finished is unknown. The marker stays open, and the audit record says so.
            await WriteAuditAsync(request, argsHash, start, ToolDispatchOutcome.Unknown, _cancelledReason, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            resultJson = _toolFailedResult;
            failure = $"tool_exception:{ex.GetType().Name}";
        }

        await _inFlight.CompleteAsync(callKey, cancellationToken);
        await WriteAuditAsync(request, argsHash, start, ToolDispatchOutcome.Executed, failure ?? decision.Reason.ToString(), cancellationToken);
        return new ToolDispatchResult(ToolDispatchOutcome.Executed, resultJson);
    }

    /// <summary>Writes the audit record for a call that did not run, then returns its result.</summary>
    private async Task<ToolDispatchResult> StopAsync(
        ToolDispatchRequest request,
        string argsHash,
        long start,
        ToolDispatchOutcome outcome,
        string reason,
        CancellationToken cancellationToken)
    {
        await WriteAuditAsync(request, argsHash, start, outcome, reason, cancellationToken);
        return new ToolDispatchResult(outcome, Reason: reason);
    }

    private async Task WriteAuditAsync(
        ToolDispatchRequest request,
        string argsHash,
        long start,
        ToolDispatchOutcome outcome,
        string reason,
        CancellationToken cancellationToken)
    {
        // Arguments are never logged, only their hash, so a secret in an argument cannot reach the audit trail.
        var payload = new JsonObject
        {
            ["agent_id"] = request.AgentId.ToString("D"),
            ["turn_id"] = request.TurnId.ToString("D"),
            ["session_id"] = request.SessionId.ToString("D"),
            ["tool"] = request.Call.Name,
            ["args_sha256"] = argsHash,
            ["outcome"] = outcome.ToString(),
            ["reason"] = reason,
            ["duration_ms"] = (long)_time.GetElapsedTime(start).TotalMilliseconds,
        };

        await _auditLog.AppendAsync(
            new AuditEntry(_time.GetUtcNow(), $"agent:{request.AgentId:D}", _auditKind, payload.ToJsonString()),
            cancellationToken);
    }

    /// <summary>Parses the arguments as one JSON object. Anything else is malformed for a tool call.</summary>
    private static bool TryParseArguments(string json, out JsonElement args)
    {
        try
        {
            args = JsonElement.Parse(json);
            return args.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            args = default;
            return false;
        }
    }

    private static string HashArguments(string json) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static SessionRoles ToSessionRoles(TurnRole role) => role switch
    {
        TurnRole.Actor => SessionRoles.Actor,
        TurnRole.Reader => SessionRoles.Reader,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown turn role."),
    };
}
