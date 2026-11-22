using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Engines;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.EngineDispatch;

/// <summary>
/// Server half of the Studio path (doc 20 WP4): authorises an agent's engine tool call and audits every write and exec decision.
/// Order: known tool, read passes, grant, hard taint and owner approval for exec, host allowlist. Any failure to ask a
/// seam denies (fail closed). The audit entry and GC message are written before the call may run.
/// </summary>
public sealed class EngineToolDispatcher
{
    /// <summary>Audit kind for every write and exec decision.</summary>
    public const string AuditKind = "engine.tool.call";

    private readonly IEngineGrantReader _grants;
    private readonly ISessionTaintReader _taint;
    private readonly IEngineGcRecorder _recorder;
    private readonly IAuditLog _audit;
    private readonly EngineDispatchOptions _options;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _hosts;

    public EngineToolDispatcher(
        IEngineGrantReader grants,
        ISessionTaintReader taint,
        IEngineGcRecorder recorder,
        IAuditLog audit,
        EngineDispatchOptions? options = null,
        TimeProvider? time = null)
    {
        _grants = grants;
        _taint = taint;
        _recorder = recorder;
        _audit = audit;
        _options = options ?? new EngineDispatchOptions();
        _time = time ?? TimeProvider.System;
        _hosts = _options.HttpAllowlist.Select(HostAllowlist.Normalize).Where(h => h.Length > 0).ToHashSet(StringComparer.Ordinal);
    }

    // Swap for Bazaar.Contracts.Engines.StudioToolCatalog.TryGetRiskClass at merge.
    private static bool LookupTool(string? tool, out RiskClass riskClass) => StudioToolCatalog.TryGetRiskClass(tool, out riskClass);

    /// <summary>
    /// The call the wire layer makes for <c>IStudioGrantVerifier</c> (stream registration is a follow-up, doc 02): true only
    /// when the tool is known at that risk class and a grant belonging to the agent covers it. Fails closed.
    /// </summary>
    public async Task<bool> VerifyGrantAsync(Guid agentId, string adapterId, string grantId, string tool, RiskClass riskClass, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(grantId) || !LookupTool(tool, out var actual) || actual != riskClass)
        {
            return false;
        }

        if (riskClass == RiskClass.Read)
        {
            return true;
        }

        return await ReadAsync(token => _grants.CoversAsync(agentId, adapterId, tool, riskClass, grantId, token), ct) is true;
    }

    /// <summary>Decides one call. Throws only <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled.</summary>
    public async Task<EngineDispatchDecision> AuthorizeAsync(EngineDispatchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (!LookupTool(request.Tool, out var risk))
        {
            return EngineDispatchDecision.Deny(EngineDispatchReason.UnknownTool);
        }

        if (risk == RiskClass.Read)
        {
            return EngineDispatchDecision.Allow(risk);
        }

        var decision = await DecideAsync(request, risk, ct);
        return await RecordAsync(request, risk, decision, ct);
    }

    private async Task<EngineDispatchDecision> DecideAsync(EngineDispatchRequest r, RiskClass risk, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.GrantId))
        {
            return EngineDispatchDecision.Deny(EngineDispatchReason.GrantMissing, risk);
        }

        var covers = await ReadAsync(t => _grants.CoversAsync(r.AgentId, r.AdapterId, r.Tool, risk, r.GrantId, t), ct);
        if (covers is null)
        {
            return EngineDispatchDecision.Deny(EngineDispatchReason.ReaderFailure, risk);
        }

        if (!covers.Value)
        {
            return EngineDispatchDecision.Deny(EngineDispatchReason.GrantNotCovering, risk);
        }

        if (risk == RiskClass.Exec)
        {
            var tainted = await ReadAsync(t => _taint.IsHardTaintedAsync(r.AgentId, r.SessionId, t), ct);
            if (tainted is null)
            {
                return EngineDispatchDecision.Deny(EngineDispatchReason.ReaderFailure, risk);
            }

            if (tainted.Value && !r.OwnerApprovalVerified)
            {
                return EngineDispatchDecision.Deny(EngineDispatchReason.HardTaintNeedsApproval, risk);
            }

            var needsApproval = _options.RobloxExecOwnerApproval && string.Equals(r.AdapterId, "roblox", StringComparison.OrdinalIgnoreCase);
            if (needsApproval && !r.OwnerApprovalVerified)
            {
                return EngineDispatchDecision.Deny(EngineDispatchReason.OwnerApprovalRequired, risk);
            }
        }

        if (!HostsAllowed(r))
        {
            return EngineDispatchDecision.Deny(EngineDispatchReason.HostNotAllowlisted, risk);
        }

        return EngineDispatchDecision.Allow(risk);
    }

    private bool HostsAllowed(EngineDispatchRequest r)
    {
        if (r.Tool == "http_get")
        {
            var url = HostAllowlist.HttpGetUrl(r.Args);
            return url is not null && HostAllowlist.IsAllowed(url, _hosts);
        }

        if (r.Tool == "execute_luau")
        {
            return HostAllowlist.FindUrls(r.Args).All(u => HostAllowlist.IsAllowed(u, _hosts));
        }

        return true;
    }

    /// <summary>Runs a seam read under the reader timeout. Null means the read failed or timed out; caller cancellation propagates.</summary>
    private async Task<bool?> ReadAsync(Func<CancellationToken, ValueTask<bool>> read, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.ReaderTimeout);
        try
        {
            return await read(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<EngineDispatchDecision> RecordAsync(EngineDispatchRequest r, RiskClass risk, EngineDispatchDecision decision, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(r.Args.ValueKind == JsonValueKind.Undefined ? "" : r.Args.GetRawText())));
        try
        {
            await _audit.AppendAsync(Entry(r, risk, decision, hash), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return EngineDispatchDecision.Deny(EngineDispatchReason.AuditFailure, risk);
        }

        try
        {
            await _recorder.RecordAsync(new EngineToolCallRecord(r.AgentId, r.AdapterId, r.Tool, risk, r.GrantId, decision.Allowed, decision.Reason, hash), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var denied = EngineDispatchDecision.Deny(EngineDispatchReason.RecorderFailure, risk);
            if (decision.Allowed)
            {
                // The audit row says allowed; leave a correcting row. Best effort, the call is denied either way.
                try
                {
                    await _audit.AppendAsync(Entry(r, risk, denied, hash), CancellationToken.None);
                }
                catch (Exception)
                {
                }
            }

            return denied;
        }

        return decision;
    }

    private AuditEntry Entry(EngineDispatchRequest r, RiskClass risk, EngineDispatchDecision d, string argsHash)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("tool", r.Tool);
            w.WriteString("adapter", r.AdapterId);
            w.WriteString("risk_class", risk.ToString().ToLowerInvariant());
            w.WriteString("grant_id", r.GrantId);
            w.WriteString("decision", d.Allowed ? "allowed" : "denied");
            w.WriteString("reason", d.Reason.ToString());
            w.WriteString("args_sha256", argsHash);
            w.WriteEndObject();
        }

        return new AuditEntry(_time.GetUtcNow(), r.AgentId.ToString("D"), AuditKind, Encoding.UTF8.GetString(ms.ToArray()));
    }
}
