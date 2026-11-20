using Bazaar.RateLimits;
using Bazaar.Server.Mail;
using Bazaar.Server.MailBackends.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.MailOutbound.Tests;

public sealed class OutboundMailPipelineTests
{
    private const string _secretBody = "the-secret-body-text";
    private const string _subject = "a private subject";
    private static readonly DateTimeOffset _t0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public TestClock Clock { get; } = new(_t0);
        public FakeSettings Settings { get; }
        public FakeApprovals Approvals { get; } = new();
        public FakeBackend Backend { get; } = new();
        public FakeAuditLog Audit { get; } = new();
        public InMemoryOutboundMailStore Store { get; } = new();
        public OutboundPipelineOptions Options { get; init; } = new(MaxAttempts: 3, RetryBaseDelay: TimeSpan.FromSeconds(10));
        public CountingPolicy Policy { get; }

        public Rig(IRateLimiter? limiter = null, FakeSettings? settings = null, FakeDirectory? directory = null)
        {
            Settings = settings ?? new FakeSettings();
            Policy = new CountingPolicy(new OutboundMailPolicy(
                directory ?? new FakeDirectory(), new FakeApproved("friend@example.org"), limiter ?? new InMemoryRateLimiter(Clock), Settings));
        }

        /// <summary>A pipeline instance over this rig's store, as a restarted process would build one.</summary>
        public OutboundMailPipeline Pipeline(IOutboundMailStore? store = null, IMailBackend? backend = null) =>
            new(Policy, store ?? Store, Approvals, new FakeResolver(backend ?? Backend), Audit, Options, Clock);
    }

    private static OutboundRequest Request(
        MailTaint taint = MailTaint.Clean, string nonce = "n1", string[]? to = null, string body = _secretBody) =>
        new("agent-1", new MailboxRef("mail.test", "charles"), to ?? ["friend@example.org"], _subject, body, 0, 0, taint, UsesTemplate: true,
            DerivedFromInboundMail: false, IsSignUpReply: false, nonce);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Tier1_send_stores_the_message_then_sends_with_the_policy_footer()
    {
        var rig = new Rig();

        var result = await rig.Pipeline().SendAsync(Request(), Ct);

        Assert.Equal(OutboundSendStatus.Sent, result.Status);
        Assert.NotNull(result.MessageId);
        Assert.Equal(1, result.Attempts);
        var mail = Assert.Single(rig.Backend.Mails);
        Assert.Equal(result.MessageId, mail.MessageId);
        var stored = await rig.Store.FindAsync(result.IdempotencyKey, Ct);
        Assert.Equal(OutboundStatus.Sent, stored!.Status);
        Assert.Equal("backend-1", stored.BackendMessageId);
    }

    [Fact]
    public async Task Footer_is_always_on_the_message_that_leaves_and_comes_from_the_policy()
    {
        var rig = new Rig();
        rig.Settings.FooterWording = "   "; // blank falls back to the shipped footer, never to none

        await rig.Pipeline().SendAsync(Request(), Ct);

        var mail = Assert.Single(rig.Backend.Mails);
        Assert.Equal(OutboundDefaults.Footer, mail.Footer);
        // The real MIME builder, with the back end's own default footer being something else, still uses the pipeline's footer.
        var mime = OutboundMessageBuilder.Build(mail, string.IsNullOrWhiteSpace(mail.Footer) ? "back end default" : mail.Footer);
        var text = mime.TextBody;
        Assert.Contains(OutboundDefaults.Footer, text, StringComparison.Ordinal);
        Assert.DoesNotContain("back end default", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_key_sends_once_and_evaluates_the_policy_once()
    {
        var rig = new Rig();
        var pipeline = rig.Pipeline();

        var first = await pipeline.SendAsync(Request(), Ct);
        var second = await pipeline.SendAsync(Request(), Ct);

        Assert.Equal(OutboundSendStatus.Sent, first.Status);
        Assert.Equal(OutboundSendStatus.AlreadySent, second.Status);
        Assert.Equal(first.MessageId, second.MessageId);
        Assert.Equal(1, rig.Backend.Calls);
        Assert.Equal(1, rig.Policy.Evaluations);
    }

    [Fact]
    public async Task A_repeated_key_still_sends_once_after_a_restart()
    {
        var rig = new Rig();
        await rig.Pipeline().SendAsync(Request(), Ct);

        // A new process: new pipeline, new back end object, same durable store.
        var freshBackend = new FakeBackend();
        var again = await rig.Pipeline(backend: freshBackend).SendAsync(Request(), Ct);

        Assert.Equal(OutboundSendStatus.AlreadySent, again.Status);
        Assert.Equal(0, freshBackend.Calls);
        Assert.Equal(1, rig.Policy.Evaluations);
    }

    [Fact]
    public async Task Different_nonce_is_a_second_deliberate_send()
    {
        var rig = new Rig();
        var pipeline = rig.Pipeline();

        await pipeline.SendAsync(Request(nonce: "a"), Ct);
        var second = await pipeline.SendAsync(Request(nonce: "b"), Ct);

        Assert.Equal(OutboundSendStatus.Sent, second.Status);
        Assert.Equal(2, rig.Backend.Delivered);
    }

    [Fact]
    public async Task Hard_tainted_send_waits_for_approval_and_sends_after_it_without_a_new_evaluation()
    {
        var rig = new Rig();
        var pipeline = rig.Pipeline();

        var held = await pipeline.SendAsync(Request(MailTaint.Hard), Ct);

        Assert.Equal(OutboundSendStatus.AwaitingApproval, held.Status);
        Assert.Equal(OutboundReason.HardTaint, held.Reason);
        Assert.Equal(0, rig.Backend.Calls);
        var (approvalId, card) = Assert.Single(rig.Approvals.Requests);
        Assert.Equal(approvalId, held.ApprovalId);
        Assert.Equal(held.IdempotencyKey, card.IdempotencyKey);
        Assert.Equal("Custom footer.", card.FooterPreview);

        // Still pending: nothing runs.
        Assert.Equal(OutboundSendStatus.AwaitingApproval, (await pipeline.ResumeAsync(held.IdempotencyKey, Ct))!.Status);
        Assert.Equal(0, rig.Backend.Calls);

        rig.Approvals.Set(approvalId, MailApprovalStatus.Approved);
        var sent = await pipeline.ResumeAsync(held.IdempotencyKey, Ct);

        Assert.Equal(OutboundSendStatus.Sent, sent!.Status);
        Assert.Equal(1, rig.Backend.Calls);
        Assert.Equal(1, rig.Approvals.ConsumeCalls);
        Assert.Equal(1, rig.Policy.Evaluations);
        Assert.Single(rig.Approvals.Requests);
    }

    [Fact]
    public async Task An_approval_does_not_carry_over_to_a_second_message()
    {
        var rig = new Rig();
        var pipeline = rig.Pipeline();
        var first = await pipeline.SendAsync(Request(MailTaint.Hard, nonce: "one"), Ct);
        rig.Approvals.Set(first.ApprovalId!.Value, MailApprovalStatus.Approved);
        await pipeline.ResumeAsync(first.IdempotencyKey, Ct);

        var second = await pipeline.SendAsync(Request(MailTaint.Hard, nonce: "two"), Ct);

        Assert.Equal(OutboundSendStatus.AwaitingApproval, second.Status);
        Assert.NotEqual(first.ApprovalId, second.ApprovalId);
        Assert.Equal(2, rig.Approvals.Requests.Count);
        Assert.Equal(1, rig.Backend.Delivered);
    }

    [Fact]
    public async Task A_refused_approval_request_ends_the_send_for_good()
    {
        var rig = new Rig();
        rig.Approvals.RefuseWith = "approval_refused:not_permitted";
        var pipeline = rig.Pipeline();

        var first = await pipeline.SendAsync(Request(MailTaint.Hard), Ct);
        var resumed = await pipeline.ResumeAsync(first.IdempotencyKey, Ct);
        var due = await pipeline.ResumeDueAsync(10, Ct);
        var repeated = await pipeline.SendAsync(Request(MailTaint.Hard), Ct);

        Assert.Equal(OutboundSendStatus.Failed, first.Status);
        Assert.Equal(OutboundSendStatus.Failed, resumed!.Status);
        Assert.Equal(OutboundSendStatus.Failed, repeated.Status);
        Assert.Equal(0, due);
        Assert.Equal(1, rig.Approvals.RequestCalls);
        Assert.Equal(0, rig.Backend.Calls);
        Assert.Equal(1, rig.Policy.Evaluations);
        Assert.Equal(OutboundStatus.Failed, (await rig.Store.FindAsync(first.IdempotencyKey, Ct))!.Status);
        var failed = Assert.Single(rig.Audit.Entries, e => e.Kind == "mail.outbound.failed");
        Assert.Contains("approval_refused:not_permitted", failed.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(_secretBody, failed.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refusal_code_is_bounded_and_plain()
    {
        Assert.Equal(MailApprovalRefusedException.DefaultReasonCode, new MailApprovalRefusedException("has spaces and a@b.example").ReasonCode);
        Assert.Equal(MailApprovalRefusedException.DefaultReasonCode, new MailApprovalRefusedException("").ReasonCode);
        Assert.Equal(MailApprovalRefusedException.MaxReasonCodeLength, new MailApprovalRefusedException(new string('a', 500)).ReasonCode.Length);
    }

    [Theory]
    [InlineData(MailApprovalStatus.Denied, OutboundSendStatus.Rejected)]
    [InlineData(MailApprovalStatus.Expired, OutboundSendStatus.Expired)]
    public async Task A_refused_or_expired_approval_is_terminal_and_never_sends(MailApprovalStatus approval, OutboundSendStatus expected)
    {
        var rig = new Rig();
        var pipeline = rig.Pipeline();
        var held = await pipeline.SendAsync(Request(MailTaint.Hard), Ct);
        rig.Approvals.Set(held.ApprovalId!.Value, approval);

        var resolved = await pipeline.ResumeAsync(held.IdempotencyKey, Ct);
        var repeated = await pipeline.SendAsync(Request(MailTaint.Hard), Ct);

        Assert.Equal(expected, resolved!.Status);
        Assert.Equal(expected, repeated.Status);
        Assert.Equal(0, rig.Backend.Calls);
        Assert.Equal(1, rig.Policy.Evaluations);
        Assert.Null((await rig.Store.FindAsync(held.IdempotencyKey, Ct))!.Request);
    }

    [Fact]
    public async Task Refuse_returns_the_reason_and_stores_and_sends_nothing()
    {
        var rig = new Rig(directory: new FakeDirectory("friend@example.org"));

        var result = await rig.Pipeline().SendAsync(Request(), Ct);

        Assert.Equal(OutboundSendStatus.Refused, result.Status);
        Assert.Equal(OutboundReason.BazaarToBazaar, result.Reason);
        Assert.Equal(0, rig.Backend.Calls);
        Assert.Empty(rig.Approvals.Requests);
        Assert.Contains("mail.outbound.refused", rig.Audit.Kinds);
    }

    [Fact]
    public async Task Pause_returns_retry_after_and_stores_nothing()
    {
        var settings = new FakeSettings { SentPerAgentDay = 1 };
        var rig = new Rig(settings: settings);
        var pipeline = rig.Pipeline();
        await pipeline.SendAsync(Request(nonce: "a"), Ct);

        var paused = await pipeline.SendAsync(Request(nonce: "b"), Ct);

        Assert.Equal(OutboundSendStatus.Paused, paused.Status);
        Assert.Equal(OutboundReason.AgentDailyQuota, paused.Reason);
        Assert.True(paused.RetryAfter > TimeSpan.Zero);
        Assert.Equal(1, rig.Backend.Delivered);
        Assert.Null(await rig.Store.FindAsync(OutboundIdempotency.Derive(Request(nonce: "b")), Ct));
    }

    [Fact]
    public async Task A_crash_between_the_insert_and_the_send_resumes_without_a_double_send()
    {
        var rig = new Rig();
        // The first update is the attempt claim; crashing there leaves the row stored in Sending and the back end untouched.
        var crashing = rig.Pipeline(new CrashingStore(rig.Store, surviveUpdates: 0));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.SendAsync(Request(), Ct));
        Assert.Equal(0, rig.Backend.Calls);
        var key = OutboundIdempotency.Derive(Request());
        Assert.Equal(OutboundStatus.Sending, (await rig.Store.FindAsync(key, Ct))!.Status);

        var restarted = rig.Pipeline();
        var resumed = await restarted.ResumeAsync(key, Ct);
        var again = await restarted.ResumeAsync(key, Ct);

        Assert.Equal(OutboundSendStatus.Sent, resumed!.Status);
        Assert.Equal(OutboundSendStatus.Sent, again!.Status);
        Assert.Equal(1, rig.Backend.Calls);
        Assert.Equal(1, rig.Backend.Delivered);
        Assert.Equal(1, rig.Policy.Evaluations);
    }

    [Fact]
    public async Task A_crash_after_the_back_end_accepted_it_resends_under_the_same_key_and_delivers_once()
    {
        var rig = new Rig();
        // Claim survives, the Sent write crashes: the back end has the message, the store does not know.
        var crashing = rig.Pipeline(new CrashingStore(rig.Store, surviveUpdates: 1));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.SendAsync(Request(), Ct));
        var key = OutboundIdempotency.Derive(Request());

        // The claim is fresh, so a resumer leaves it alone until it goes stale.
        var early = await rig.Pipeline().ResumeAsync(key, Ct);
        Assert.Equal(OutboundSendStatus.RetryScheduled, early!.Status);
        Assert.Equal(1, rig.Backend.Calls);

        rig.Clock.Advance(TimeSpan.FromMinutes(6));
        var resumed = await rig.Pipeline().ResumeAsync(key, Ct);

        Assert.Equal(OutboundSendStatus.Sent, resumed!.Status);
        Assert.Equal(2, rig.Backend.Calls); // at least once toward the back end ...
        Assert.Equal(1, rig.Backend.Delivered); // ... exactly once delivered, because the key is the back end's idempotency key
        Assert.Equal("backend-1", (await rig.Store.FindAsync(key, Ct))!.BackendMessageId);
    }

    [Fact]
    public async Task Failed_sends_back_off_exponentially_and_stop_at_the_attempt_cap()
    {
        var rig = new Rig();
        rig.Backend.FailNext = 99;
        var pipeline = rig.Pipeline();

        var first = await pipeline.SendAsync(Request(), Ct);
        Assert.Equal(OutboundSendStatus.RetryScheduled, first.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), first.RetryAfter);

        // Not due yet: no back end call.
        rig.Clock.Advance(TimeSpan.FromSeconds(4));
        var early = await pipeline.ResumeAsync(first.IdempotencyKey, Ct);
        Assert.Equal(OutboundSendStatus.RetryScheduled, early!.Status);
        Assert.Equal(TimeSpan.FromSeconds(6), early.RetryAfter);
        Assert.Equal(1, rig.Backend.Calls);

        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        var second = await pipeline.ResumeAsync(first.IdempotencyKey, Ct);
        Assert.Equal(OutboundSendStatus.RetryScheduled, second!.Status);
        Assert.Equal(TimeSpan.FromSeconds(20), second.RetryAfter);

        rig.Clock.Advance(TimeSpan.FromSeconds(20));
        var last = await pipeline.ResumeAsync(first.IdempotencyKey, Ct);
        Assert.Equal(OutboundSendStatus.Failed, last!.Status);
        Assert.Equal(3, rig.Backend.Calls);
        Assert.Equal(0, rig.Backend.Delivered);

        rig.Clock.Advance(TimeSpan.FromDays(1));
        await pipeline.ResumeAsync(first.IdempotencyKey, Ct);
        Assert.Equal(3, rig.Backend.Calls);
        Assert.Null((await rig.Store.FindAsync(first.IdempotencyKey, Ct))!.Request);
    }

    [Fact]
    public async Task A_missing_back_end_counts_as_a_failed_attempt()
    {
        var rig = new Rig();
        var pipeline = new OutboundMailPipeline(rig.Policy, rig.Store, rig.Approvals, new FakeResolver(null), rig.Audit, rig.Options, rig.Clock);

        var result = await pipeline.SendAsync(Request(), Ct);

        Assert.Equal(OutboundSendStatus.RetryScheduled, result.Status);
    }

    [Fact]
    public void Backoff_doubles_and_is_capped()
    {
        var options = new OutboundPipelineOptions(RetryBaseDelay: TimeSpan.FromSeconds(30), RetryMaxDelay: TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromSeconds(30), options.DelayAfter(1));
        Assert.Equal(TimeSpan.FromSeconds(60), options.DelayAfter(2));
        Assert.Equal(TimeSpan.FromMinutes(5), options.DelayAfter(9));
        Assert.Equal(TimeSpan.FromMinutes(5), options.DelayAfter(500));
        Assert.Equal(10, new OutboundPipelineOptions(MaxAttempts: 1000).EffectiveMaxAttempts);
        Assert.Equal(1, new OutboundPipelineOptions(MaxAttempts: 0).EffectiveMaxAttempts);
    }

    [Fact]
    public async Task ResumeDue_advances_pending_and_approved_sends_only()
    {
        var rig = new Rig();
        rig.Backend.FailNext = 1;
        var pipeline = rig.Pipeline();
        var failing = await pipeline.SendAsync(Request(nonce: "retry"), Ct);
        var held = await pipeline.SendAsync(Request(MailTaint.Hard, nonce: "hold"), Ct);
        rig.Approvals.Set(held.ApprovalId!.Value, MailApprovalStatus.Approved);

        Assert.Equal(1, await pipeline.ResumeDueAsync(10, Ct)); // the approved one; the retry is not due yet
        rig.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, await pipeline.ResumeDueAsync(10, Ct));

        Assert.Equal(OutboundStatus.Sent, (await rig.Store.FindAsync(failing.IdempotencyKey, Ct))!.Status);
        Assert.Equal(OutboundStatus.Sent, (await rig.Store.FindAsync(held.IdempotencyKey, Ct))!.Status);
        Assert.Equal(0, await pipeline.ResumeDueAsync(10, Ct));
    }

    [Fact]
    public async Task Bounce_hook_marks_a_sent_message_bounced_once()
    {
        var rig = new Rig();
        var pipeline = rig.Pipeline();
        var sent = await pipeline.SendAsync(Request(), Ct);

        Assert.True(await pipeline.RecordBounceAsync(sent.IdempotencyKey, Ct));
        Assert.False(await pipeline.RecordBounceAsync(sent.IdempotencyKey, Ct));
        Assert.False(await pipeline.RecordBounceAsync("unknown", Ct));
        Assert.Equal(OutboundSendStatus.Bounced, (await pipeline.SendAsync(Request(), Ct)).Status);
        Assert.Contains("mail.outbound.bounced", rig.Audit.Kinds);
    }

    [Fact]
    public async Task Audit_rows_carry_ids_and_counts_but_no_address_subject_or_body()
    {
        var rig = new Rig();
        rig.Backend.FailNext = 1;
        var pipeline = rig.Pipeline();
        await pipeline.SendAsync(Request(), Ct);
        var held = await pipeline.SendAsync(Request(MailTaint.Hard, nonce: "h"), Ct);
        rig.Approvals.Set(held.ApprovalId!.Value, MailApprovalStatus.Denied);
        await pipeline.ResumeAsync(held.IdempotencyKey, Ct);
        await new Rig(directory: new FakeDirectory("friend@example.org")).Pipeline().SendAsync(Request(), Ct);

        Assert.NotEmpty(rig.Audit.Entries);
        foreach (var entry in rig.Audit.Entries)
        {
            Assert.Equal(OutboundMailPipeline.AuditActor, entry.Actor);
            Assert.DoesNotContain("friend@example.org", entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("example.org", entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("mail.test", entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(_secretBody, entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(_subject, entry.Payload, StringComparison.Ordinal);
        }

        var kinds = rig.Audit.Kinds.ToList();
        Assert.Contains("mail.outbound.sending", kinds);
        Assert.Contains("mail.outbound.retry_scheduled", kinds);
        Assert.Contains("mail.outbound.queued_for_approval", kinds);
        Assert.Contains("mail.outbound.approval_requested", kinds);
        Assert.Contains("mail.outbound.rejected", kinds);
    }

    [Fact]
    public async Task The_pipeline_resolves_from_the_service_collection()
    {
        var rig = new Rig();
        var services = new ServiceCollection();
        services.AddSingleton<IOutboundMailStore>(rig.Store);
        services.AddSingleton<IMailApprovalGateway>(rig.Approvals);
        services.AddSingleton<IMailBackendResolver>(new FakeResolver(rig.Backend));
        services.AddSingleton<Bazaar.Server.Security.Audit.IAuditLog>(rig.Audit);
        services.AddSingleton<IOutboundMailPolicy>(rig.Policy);
        services.AddBazaarMailOutbound();

        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IOutboundMailPipeline>().SendAsync(Request(), Ct);

        Assert.Equal(OutboundSendStatus.Sent, result.Status);
    }
}
