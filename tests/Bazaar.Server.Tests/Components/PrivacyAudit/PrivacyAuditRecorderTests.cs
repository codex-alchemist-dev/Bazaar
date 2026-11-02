using System.Text.Json.Nodes;
using Bazaar.Server.Components.PrivacyAudit;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Tests.Components.PrivacyAudit;

/// <summary>Each recorder method writes one entry with its kind, a principal actor, the injected timestamp and the expected payload.</summary>
public sealed class PrivacyAuditRecorderTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static (PrivacyAuditRecorder Recorder, FakeAuditLog Log, ManualTime Time) Create()
    {
        var log = new FakeAuditLog();
        var time = new ManualTime(_start);
        return (new PrivacyAuditRecorder(log, time), log, time);
    }

    private static JsonObject PayloadOf(AuditEntry entry) => JsonNode.Parse(entry.Payload)!.AsObject();

    [Fact]
    public async Task Offer_created_writes_kind_actor_and_role_ttl_fields()
    {
        var (recorder, log, _) = Create();

        await recorder.OfferCreatedAsync("human:owner", "offer-1", "member", 10, TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.PairingOfferCreated, entry.Kind);
        Assert.Equal("human:owner", entry.Actor);
        var payload = PayloadOf(entry);
        Assert.Equal("offer-1", payload["offer_id"]!.GetValue<string>());
        Assert.Equal("member", payload["role"]!.GetValue<string>());
        Assert.Equal(10, payload["ttl_minutes"]!.GetValue<int>());
    }

    [Fact]
    public async Task Pairing_issued_writes_offer_device_and_class()
    {
        var (recorder, log, _) = Create();

        await recorder.PairingIssuedAsync("human:owner", "offer-1", "device-7", "android", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.PairingIssued, entry.Kind);
        var payload = PayloadOf(entry);
        Assert.Equal("offer-1", payload["offer_id"]!.GetValue<string>());
        Assert.Equal("device-7", payload["device_id"]!.GetValue<string>());
        Assert.Equal("android", payload["device_class"]!.GetValue<string>());
    }

    [Fact]
    public async Task Device_revoked_writes_device_and_reason()
    {
        var (recorder, log, _) = Create();

        await recorder.DeviceRevokedAsync("human:owner", "device-7", "owner_request", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.DeviceRevoked, entry.Kind);
        Assert.Equal("device-7", PayloadOf(entry)["device_id"]!.GetValue<string>());
        Assert.Equal("owner_request", PayloadOf(entry)["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Token_reuse_detected_writes_device_and_credential_record_id()
    {
        var (recorder, log, _) = Create();

        await recorder.TokenReuseDetectedAsync("device:device-7", "device-7", "cred-42", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.TokenReuseDetected, entry.Kind);
        Assert.Equal("device:device-7", entry.Actor);
        Assert.Equal("cred-42", PayloadOf(entry)["credential_record_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sign_in_succeeded_writes_human_and_method()
    {
        var (recorder, log, _) = Create();

        await recorder.SignInSucceededAsync("human:owner", "human:owner", "passkey", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.SignInSucceeded, entry.Kind);
        Assert.Equal("human:owner", PayloadOf(entry)["human_id"]!.GetValue<string>());
        Assert.Equal("passkey", PayloadOf(entry)["method"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sign_in_failed_uses_anonymous_actor_and_only_a_reason()
    {
        var (recorder, log, _) = Create();

        await recorder.SignInFailedAsync(PrivacyAuditRecorder.AnonymousActor, "bad_assertion", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.SignInFailed, entry.Kind);
        Assert.Equal("anonymous", entry.Actor);
        var payload = PayloadOf(entry);
        Assert.Single(payload);
        Assert.Equal("bad_assertion", payload["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Recovery_code_used_logs_the_record_id_not_the_code()
    {
        var (recorder, log, _) = Create();

        await recorder.RecoveryCodeUsedAsync("human:owner", "human:owner", "rec-3", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.RecoveryCodeUsed, entry.Kind);
        var payload = PayloadOf(entry);
        Assert.Equal("rec-3", payload["record_id"]!.GetValue<string>());
        Assert.DoesNotContain("code", entry.Payload.Replace("record_id", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Access_request_requested_writes_request_and_gc()
    {
        var (recorder, log, _) = Create();

        await recorder.AccessRequestRequestedAsync("human:coworker", "req-1", "dm-9", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.AccessRequestRequested, entry.Kind);
        Assert.Equal("req-1", PayloadOf(entry)["request_id"]!.GetValue<string>());
        Assert.Equal("dm-9", PayloadOf(entry)["gc_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("granted")]
    [InlineData("denied")]
    public async Task Access_request_decided_writes_granted_or_denied(string decision)
    {
        var (recorder, log, _) = Create();

        await recorder.AccessRequestDecidedAsync("human:owner", "req-1", "dm-9", decision, TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.AccessRequestDecided, entry.Kind);
        Assert.Equal(decision, PayloadOf(entry)["decision"]!.GetValue<string>());
    }

    [Fact]
    public async Task Access_request_decided_rejects_other_decisions_and_writes_nothing()
    {
        var (recorder, log, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => recorder.AccessRequestDecidedAsync("human:owner", "req-1", "dm-9", "maybe", TestContext.Current.CancellationToken));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Role_changed_writes_human_old_and_new_role()
    {
        var (recorder, log, _) = Create();

        await recorder.RoleChangedAsync("human:owner", "human:coworker", "member", "admin", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.RoleChanged, entry.Kind);
        var payload = PayloadOf(entry);
        Assert.Equal("human:coworker", payload["human_id"]!.GetValue<string>());
        Assert.Equal("member", payload["old_role"]!.GetValue<string>());
        Assert.Equal("admin", payload["new_role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Owner_dm_read_writes_gc_and_from_seq()
    {
        var (recorder, log, _) = Create();

        await recorder.OwnerDmReadAsync("human:owner", "dm-9", 120, TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.OwnerDmRead, entry.Kind);
        Assert.Equal("dm-9", PayloadOf(entry)["gc_id"]!.GetValue<string>());
        Assert.Equal(120, PayloadOf(entry)["from_seq"]!.GetValue<long>());
    }

    [Fact]
    public async Task Export_completed_writes_export_id_and_head_hash()
    {
        var (recorder, log, _) = Create();

        await recorder.ExportCompletedAsync("system:audit-export", "exp-5", "ab12cd34", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.ExportCompleted, entry.Kind);
        Assert.Equal("exp-5", PayloadOf(entry)["export_id"]!.GetValue<string>());
        Assert.Equal("ab12cd34", PayloadOf(entry)["head_hash"]!.GetValue<string>());
    }

    [Fact]
    public async Task Erasure_completed_writes_person_and_plan()
    {
        var (recorder, log, _) = Create();

        await recorder.ErasureCompletedAsync("human:owner", "person-2", "plan-8", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.ErasureCompleted, entry.Kind);
        Assert.Equal("person-2", PayloadOf(entry)["person_id"]!.GetValue<string>());
        Assert.Equal("plan-8", PayloadOf(entry)["plan_id"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("denied")]
    public async Task Approval_decided_writes_decision_action_and_args_hash(string decision)
    {
        var (recorder, log, _) = Create();

        await recorder.ApprovalDecidedAsync("human:owner", "appr-1", "merge_pr", decision, "f00d", "device-7", TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.ApprovalDecided, entry.Kind);
        var payload = PayloadOf(entry);
        Assert.Equal("appr-1", payload["approval_id"]!.GetValue<string>());
        Assert.Equal("merge_pr", payload["action"]!.GetValue<string>());
        Assert.Equal(decision, payload["decision"]!.GetValue<string>());
        Assert.Equal("f00d", payload["args_hash"]!.GetValue<string>());
        Assert.Equal("device-7", payload["device_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Approval_decided_rejects_an_unknown_decision()
    {
        var (recorder, log, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => recorder.ApprovalDecidedAsync("human:owner", "appr-1", "merge_pr", "yes", "f00d", "device-7", TestContext.Current.CancellationToken));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Approval_key_events_write_enrolment_and_device_or_approval()
    {
        var (recorder, log, _) = Create();

        await recorder.ApprovalKeyEnrolledAsync("human:owner", "enr-1", "device-7", TestContext.Current.CancellationToken);
        await recorder.ApprovalKeyUsedAsync("human:owner", "enr-1", "appr-1", TestContext.Current.CancellationToken);
        await recorder.ApprovalKeyRevokedAsync("human:owner", "enr-1", "device-7", TestContext.Current.CancellationToken);

        var kinds = log.Entries.Select(e => e.Kind).ToArray();
        Assert.Equal(new[] { PrivacyAuditKinds.ApprovalKeyEnrolled, PrivacyAuditKinds.ApprovalKeyUsed, PrivacyAuditKinds.ApprovalKeyRevoked }, kinds);
        Assert.Equal("device-7", PayloadOf(log.Entries[0])["device_id"]!.GetValue<string>());
        Assert.Equal("appr-1", PayloadOf(log.Entries[1])["approval_id"]!.GetValue<string>());
        Assert.Equal("device-7", PayloadOf(log.Entries[2])["device_id"]!.GetValue<string>());
        Assert.All(log.Entries, e => Assert.Equal("enr-1", PayloadOf(e)["enrolment_id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Rate_limit_denied_writes_bucket_limit_and_window()
    {
        var (recorder, log, _) = Create();

        await recorder.RateLimitDeniedAsync("anonymous", "signin", 10, 600, TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(PrivacyAuditKinds.RateLimitDenied, entry.Kind);
        var payload = PayloadOf(entry);
        Assert.Equal("signin", payload["bucket"]!.GetValue<string>());
        Assert.Equal(10, payload["limit"]!.GetValue<int>());
        Assert.Equal(600, payload["window_seconds"]!.GetValue<int>());
    }

    [Fact]
    public async Task Timestamps_come_from_the_injected_time_provider()
    {
        var (recorder, log, time) = Create();

        await recorder.SignInFailedAsync(PrivacyAuditRecorder.AnonymousActor, "bad_assertion", TestContext.Current.CancellationToken);
        time.Now = _start.AddMinutes(5);
        await recorder.SignInFailedAsync(PrivacyAuditRecorder.AnonymousActor, "bad_assertion", TestContext.Current.CancellationToken);

        Assert.Equal(_start, log.Entries[0].Ts);
        Assert.Equal(_start.AddMinutes(5), log.Entries[1].Ts);
    }

    [Fact]
    public async Task Payload_is_canonical_json_with_keys_sorted()
    {
        var (recorder, log, _) = Create();

        await recorder.OfferCreatedAsync("human:owner", "offer-1", "member", 10, TestContext.Current.CancellationToken);

        Assert.Equal("{\"offer_id\":\"offer-1\",\"role\":\"member\",\"ttl_minutes\":10}", log.Entries[0].Payload);
    }

    [Fact]
    public async Task Every_recorded_payload_passes_the_redaction_guard()
    {
        var (recorder, log, _) = Create();

        await recorder.OfferCreatedAsync("human:owner", "offer-1", "member", 10, TestContext.Current.CancellationToken);
        await recorder.PairingIssuedAsync("human:owner", "offer-1", "device-7", "android", TestContext.Current.CancellationToken);
        await recorder.DeviceRevokedAsync("human:owner", "device-7", "owner_request", TestContext.Current.CancellationToken);
        await recorder.TokenReuseDetectedAsync("device:device-7", "device-7", "cred-42", TestContext.Current.CancellationToken);
        await recorder.SignInSucceededAsync("human:owner", "human:owner", "passkey", TestContext.Current.CancellationToken);
        await recorder.SignInFailedAsync(PrivacyAuditRecorder.AnonymousActor, "bad_assertion", TestContext.Current.CancellationToken);
        await recorder.RecoveryCodeUsedAsync("human:owner", "human:owner", "rec-3", TestContext.Current.CancellationToken);
        await recorder.AccessRequestRequestedAsync("human:coworker", "req-1", "dm-9", TestContext.Current.CancellationToken);
        await recorder.AccessRequestDecidedAsync("human:owner", "req-1", "dm-9", "granted", TestContext.Current.CancellationToken);
        await recorder.RoleChangedAsync("human:owner", "human:coworker", "member", "admin", TestContext.Current.CancellationToken);
        await recorder.OwnerDmReadAsync("human:owner", "dm-9", 0, TestContext.Current.CancellationToken);
        await recorder.ExportCompletedAsync("system:audit-export", "exp-5", "ab12cd34", TestContext.Current.CancellationToken);
        await recorder.ErasureCompletedAsync("human:owner", "person-2", "plan-8", TestContext.Current.CancellationToken);
        await recorder.ApprovalDecidedAsync("human:owner", "appr-1", "merge_pr", "approved", "f00d", "device-7", TestContext.Current.CancellationToken);
        await recorder.ApprovalKeyEnrolledAsync("human:owner", "enr-1", "device-7", TestContext.Current.CancellationToken);
        await recorder.ApprovalKeyUsedAsync("human:owner", "enr-1", "appr-1", TestContext.Current.CancellationToken);
        await recorder.ApprovalKeyRevokedAsync("human:owner", "enr-1", "device-7", TestContext.Current.CancellationToken);
        await recorder.RateLimitDeniedAsync("anonymous", "signin", 10, 600, TestContext.Current.CancellationToken);

        Assert.Equal(PrivacyAuditKinds.All.Count, log.Entries.Count);
        foreach (var entry in log.Entries)
            AuditPayloadGuard.Ensure(PayloadOf(entry));
    }

    [Fact]
    public async Task Blank_actor_is_refused_and_nothing_is_written()
    {
        var (recorder, log, _) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => recorder.SignInFailedAsync(" ", "bad_assertion", TestContext.Current.CancellationToken));
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void Constructor_refuses_null_dependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new PrivacyAuditRecorder(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new PrivacyAuditRecorder(new FakeAuditLog(), null!));
    }

    [Fact]
    public void Kinds_are_unique_dotted_lowercase_and_match_the_documented_list()
    {
        Assert.Equal(PrivacyAuditKinds.All.Count, PrivacyAuditKinds.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(18, PrivacyAuditKinds.All.Count);
        foreach (var kind in PrivacyAuditKinds.All)
        {
            Assert.Matches("^[a-z_]+(\\.[a-z_]+)+$", kind);
        }
    }
}
