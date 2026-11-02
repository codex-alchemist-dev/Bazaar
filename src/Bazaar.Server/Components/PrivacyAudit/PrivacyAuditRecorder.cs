using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Components.PrivacyAudit;

/// <summary>
/// Writes the privacy and remote-access audit events (doc 32 section 8, doc 15 <c>IAuditLog</c>) as <see cref="AuditEntry"/>
/// rows. One typed method per event family builds its payload, so callers cannot pass free-form fields. Every payload goes
/// through <see cref="AuditPayloadGuard"/> and is written as canonical JSON (keys sorted, compact). The timestamp comes from
/// the injected <see cref="TimeProvider"/>. The actor is a principal id (for example <c>human:...</c>, <c>device:...</c> or
/// <c>system:...</c>), never a token; unauthenticated attempts use <c>anonymous</c>.
/// </summary>
public sealed class PrivacyAuditRecorder
{
    /// <summary>Actor for an attempt with no authenticated principal.</summary>
    public const string AnonymousActor = "anonymous";

    private readonly IAuditLog _log;
    private readonly TimeProvider _time;

    public PrivacyAuditRecorder(IAuditLog log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _log = log;
        _time = time;
    }

    /// <summary>A pairing offer was created by a signed-in human (doc 32 section 3 step 1).</summary>
    public Task OfferCreatedAsync(string actorId, string offerId, string role, int ttlMinutes, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.PairingOfferCreated, new JsonObject
        {
            ["offer_id"] = Id(offerId, nameof(offerId)),
            ["role"] = Label(role, nameof(role)),
            ["ttl_minutes"] = ttlMinutes,
        }, ct);

    /// <summary>A native approval minted a device record and credentials (doc 32 section 3 step 4).</summary>
    public Task PairingIssuedAsync(string actorId, string offerId, string deviceId, string deviceClass, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.PairingIssued, new JsonObject
        {
            ["offer_id"] = Id(offerId, nameof(offerId)),
            ["device_id"] = Id(deviceId, nameof(deviceId)),
            ["device_class"] = Label(deviceClass, nameof(deviceClass)),
        }, ct);

    /// <summary>A device was revoked (doc 32 section 5). <paramref name="reason"/> is a short code-free label, not free text.</summary>
    public Task DeviceRevokedAsync(string actorId, string deviceId, string reason, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.DeviceRevoked, new JsonObject
        {
            ["device_id"] = Id(deviceId, nameof(deviceId)),
            ["reason"] = Label(reason, nameof(reason)),
        }, ct);

    /// <summary>A retired refresh credential was presented again; the device is revoked (doc 32 section 5).</summary>
    public Task TokenReuseDetectedAsync(string actorId, string deviceId, string credentialRecordId, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.TokenReuseDetected, new JsonObject
        {
            ["device_id"] = Id(deviceId, nameof(deviceId)),
            ["credential_record_id"] = Id(credentialRecordId, nameof(credentialRecordId)),
        }, ct);

    /// <summary>A human signed in (doc 32 sections 4 and 8). <paramref name="method"/> is for example <c>passkey</c>.</summary>
    public Task SignInSucceededAsync(string actorId, string humanId, string method, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.SignInSucceeded, new JsonObject
        {
            ["human_id"] = Id(humanId, nameof(humanId)),
            ["method"] = Label(method, nameof(method)),
        }, ct);

    /// <summary>A sign-in attempt failed. The payload carries only a generic reason, never the attempted name (doc 32 section 4).</summary>
    public Task SignInFailedAsync(string actorId, string reason, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.SignInFailed, new JsonObject
        {
            ["reason"] = Label(reason, nameof(reason)),
        }, ct);

    /// <summary>A printed recovery code was consumed (doc 32 section 4, <c>/signin/recover</c>). Logs the record id only.</summary>
    public Task RecoveryCodeUsedAsync(string actorId, string humanId, string recordId, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.RecoveryCodeUsed, new JsonObject
        {
            ["human_id"] = Id(humanId, nameof(humanId)),
            ["record_id"] = Id(recordId, nameof(recordId)),
        }, ct);

    /// <summary>A DM access request was filed (doc 32 section 6).</summary>
    public Task AccessRequestRequestedAsync(string actorId, string requestId, string gcId, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.AccessRequestRequested, new JsonObject
        {
            ["request_id"] = Id(requestId, nameof(requestId)),
            ["gc_id"] = Id(gcId, nameof(gcId)),
        }, ct);

    /// <summary>A DM access request was granted or denied (doc 32 section 6). <paramref name="decision"/> is <c>granted</c> or <c>denied</c>.</summary>
    public Task AccessRequestDecidedAsync(string actorId, string requestId, string gcId, string decision, CancellationToken ct = default)
    {
        if (decision is not ("granted" or "denied"))
            throw new ArgumentException("Decision must be 'granted' or 'denied'.", nameof(decision));
        return AppendAsync(actorId, PrivacyAuditKinds.AccessRequestDecided, new JsonObject
        {
            ["request_id"] = Id(requestId, nameof(requestId)),
            ["gc_id"] = Id(gcId, nameof(gcId)),
            ["decision"] = decision,
        }, ct);
    }

    /// <summary>A human's role changed (doc 32 section 8).</summary>
    public Task RoleChangedAsync(string actorId, string humanId, string oldRole, string newRole, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.RoleChanged, new JsonObject
        {
            ["human_id"] = Id(humanId, nameof(humanId)),
            ["old_role"] = Label(oldRole, nameof(oldRole)),
            ["new_role"] = Label(newRole, nameof(newRole)),
        }, ct);

    /// <summary>The owner read a DM, from <paramref name="fromSeq"/> (doc 32 sections 6 and 8).</summary>
    public Task OwnerDmReadAsync(string actorId, string gcId, long fromSeq, CancellationToken ct = default)
    {
        if (fromSeq < 0)
            throw new ArgumentOutOfRangeException(nameof(fromSeq), fromSeq, "Sequence numbers are not negative.");
        return AppendAsync(actorId, PrivacyAuditKinds.OwnerDmRead, new JsonObject
        {
            ["gc_id"] = Id(gcId, nameof(gcId)),
            ["from_seq"] = fromSeq,
        }, ct);
    }

    /// <summary>A signed export of the audit log was produced (doc 32 section 8, doc 15). Logs the head hash only.</summary>
    public Task ExportCompletedAsync(string actorId, string exportId, string headHashHex, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.ExportCompleted, new JsonObject
        {
            ["export_id"] = Id(exportId, nameof(exportId)),
            ["head_hash"] = Id(headHashHex, nameof(headHashHex)),
        }, ct);

    /// <summary>An erasure plan was executed for a person (doc 32 section 8, section 12).</summary>
    public Task ErasureCompletedAsync(string actorId, string personId, string planId, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.ErasureCompleted, new JsonObject
        {
            ["person_id"] = Id(personId, nameof(personId)),
            ["plan_id"] = Id(planId, nameof(planId)),
        }, ct);

    /// <summary>A human approval was decided (doc 15 approval flow). <paramref name="decision"/> is <c>approved</c> or <c>denied</c>.</summary>
    public Task ApprovalDecidedAsync(string actorId, string approvalId, string action, string decision, string argsHash, string deviceId, CancellationToken ct = default)
    {
        if (decision is not ("approved" or "denied"))
            throw new ArgumentException("Decision must be 'approved' or 'denied'.", nameof(decision));
        return AppendAsync(actorId, PrivacyAuditKinds.ApprovalDecided, new JsonObject
        {
            ["approval_id"] = Id(approvalId, nameof(approvalId)),
            ["action"] = Label(action, nameof(action)),
            ["decision"] = decision,
            ["args_hash"] = Id(argsHash, nameof(argsHash)),
            ["device_id"] = Id(deviceId, nameof(deviceId)),
        }, ct);
    }

    /// <summary>An owner approval key was enrolled inside pairing (doc 32 section 5a).</summary>
    public Task ApprovalKeyEnrolledAsync(string actorId, string enrolmentId, string deviceId, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.ApprovalKeyEnrolled, new JsonObject
        {
            ["enrolment_id"] = Id(enrolmentId, nameof(enrolmentId)),
            ["device_id"] = Id(deviceId, nameof(deviceId)),
        }, ct);

    /// <summary>An owner approval key signed an approval (doc 32 section 5a).</summary>
    public Task ApprovalKeyUsedAsync(string actorId, string enrolmentId, string approvalId, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.ApprovalKeyUsed, new JsonObject
        {
            ["enrolment_id"] = Id(enrolmentId, nameof(enrolmentId)),
            ["approval_id"] = Id(approvalId, nameof(approvalId)),
        }, ct);

    /// <summary>An owner approval key was revoked (doc 32 section 5a).</summary>
    public Task ApprovalKeyRevokedAsync(string actorId, string enrolmentId, string deviceId, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.ApprovalKeyRevoked, new JsonObject
        {
            ["enrolment_id"] = Id(enrolmentId, nameof(enrolmentId)),
            ["device_id"] = Id(deviceId, nameof(deviceId)),
        }, ct);

    /// <summary>A rate-limit bucket refused a request (doc 32 section 7: breaches are audited).</summary>
    public Task RateLimitDeniedAsync(string actorId, string bucket, int limit, int windowSeconds, CancellationToken ct = default) =>
        AppendAsync(actorId, PrivacyAuditKinds.RateLimitDenied, new JsonObject
        {
            ["bucket"] = Label(bucket, nameof(bucket)),
            ["limit"] = limit,
            ["window_seconds"] = windowSeconds,
        }, ct);

    private Task AppendAsync(string actorId, string kind, JsonObject fields, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        AuditPayloadGuard.Ensure(fields);
        var payload = Canonicalize(fields).ToJsonString();
        return _log.AppendAsync(new AuditEntry(_time.GetUtcNow(), actorId, kind, payload), ct);
    }

    /// <summary>
    /// Validates a short label field (role, reason, method, action, bucket, device class). Labels are lowercase slugs, so a
    /// token or a sentence cannot pass as one, whatever the payload key is called.
    /// </summary>
    private static string Label(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (!_labelPattern.IsMatch(value))
            throw new ArgumentException($"'{paramName}' must be a lowercase label of letters, digits and underscores (1 to 32 characters).", paramName);
        return value;
    }

    private static readonly System.Text.RegularExpressions.Regex _labelPattern = new(@"^[a-z][a-z0-9_]{0,31}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Validates an identifier-like field: it must be non-blank. Returned unchanged for the payload.</summary>
    private static string Id(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }

    /// <summary>Canonical form: object keys sorted ordinally at every level, no whitespace. Builds new nodes, so the input is untouched.</summary>
    private static JsonObject Canonicalize(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var name in obj.Select(kv => kv.Key).Order(StringComparer.Ordinal))
            sorted[name] = CanonicalizeNode(obj[name]);
        return sorted;
    }

    private static JsonNode? CanonicalizeNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                return Canonicalize(obj);
            case JsonArray arr:
                var array = new JsonArray();
                foreach (var item in arr)
                    array.Add(CanonicalizeNode(item));
                return array;
            default:
                return JsonNode.Parse(node.ToJsonString());
        }
    }
}
