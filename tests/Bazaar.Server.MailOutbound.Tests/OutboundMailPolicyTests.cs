using Bazaar.RateLimits;
using Bazaar.Server.Mail;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.MailOutbound.Tests;

public sealed class FakeDirectory(params string[] bazaar) : IBazaarAddressDirectory
{
    public ValueTask<bool> IsBazaarAddressAsync(string address, CancellationToken ct) =>
        ValueTask.FromResult(bazaar.Contains(address, StringComparer.OrdinalIgnoreCase));
}

public sealed class FakeApproved(params string[] approved) : IApprovedRecipients
{
    public ValueTask<bool> IsApprovedAsync(string agentId, string address, CancellationToken ct) =>
        ValueTask.FromResult(approved.Contains(address, StringComparer.OrdinalIgnoreCase));
}

public sealed class FakeSettings : IOutboundMailSettings
{
    public int NewRecipientsPerAgentDay { get; set; } = OutboundDefaults.NewRecipientsPerAgentDay;
    public int SentPerDay { get; set; } = OutboundDefaults.SentPerDay;
    public int SentPerAgentDay { get; set; } = OutboundDefaults.SentPerAgentDay;
    public string? OperatorContact { get; set; }
    public string? FooterWording { get; set; } = "Custom footer.";
    public long MaxMessageBytes { get; set; } = OutboundDefaults.MaxMessageBytes;
    public int MaxAttachments { get; set; } = OutboundDefaults.MaxAttachments;
    public long BackendHardCapBytes { get; set; } = 25L * 1024 * 1024;
}

public class OutboundMailPolicyTests
{
    private static readonly string[] _ext = ["friend@example.org"];

    private static OutboundRequest Request(
        string[]? to = null, MailTaint taint = MailTaint.Clean, bool template = true, bool inbound = false, bool signUp = false,
        int attachments = 0, long attachmentBytes = 0, string body = "hello", string nonce = "n1", string agent = "agent-1") =>
        new(agent, new MailboxRef("mail.test", "charles"), to ?? _ext, "subject", body, attachments, attachmentBytes, taint, template, inbound, signUp, nonce);

    private static OutboundMailPolicy Policy(
        FakeSettings? settings = null, FakeDirectory? directory = null, FakeApproved? approved = null, IRateLimiter? limiter = null) =>
        new(directory ?? new FakeDirectory(), approved ?? new FakeApproved("friend@example.org"),
            limiter ?? new InMemoryRateLimiter(TimeProvider.System), settings ?? new FakeSettings());

    [Fact]
    public async Task Approved_recipient_and_template_is_tier1_send()
    {
        var d = await Policy().EvaluateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.Send, d.Kind);
        Assert.Equal(1, d.Tier);
        Assert.Equal("Custom footer.", d.Footer);
    }

    [Fact]
    public async Task Approved_recipient_without_template_needs_approval()
    {
        var d = await Policy().EvaluateAsync(Request(template: false), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.NeedsOwnerApproval, d.Kind);
        Assert.Equal(OutboundReason.NotTemplate, d.Reason);
    }

    [Fact]
    public async Task Hard_taint_needs_tier2_even_for_approved_recipient_and_template()
    {
        var d = await Policy().EvaluateAsync(Request(taint: MailTaint.Hard), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.NeedsOwnerApproval, d.Kind);
        Assert.Equal(OutboundReason.HardTaint, d.Reason);
        Assert.Equal(2, d.Tier);
    }

    [Fact]
    public async Task Soft_taint_changes_no_tier()
    {
        var d = await Policy().EvaluateAsync(Request(taint: MailTaint.Soft), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.Send, d.Kind);
    }

    [Fact]
    public async Task Hard_taint_stays_tier2_whatever_the_settings_say()
    {
        // No setting field reaches the hard-taint gate; even the loosest values leave it in place.
        var loose = new FakeSettings { NewRecipientsPerAgentDay = int.MaxValue, SentPerDay = int.MaxValue, MaxAttachments = int.MaxValue };
        var d = await Policy(loose).EvaluateAsync(Request(taint: MailTaint.Hard), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.NeedsOwnerApproval, d.Kind);
    }

    [Fact]
    public async Task New_recipient_needs_approval()
    {
        var d = await Policy().EvaluateAsync(Request(to: ["stranger@example.net"]), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundReason.NewRecipient, d.Reason);
    }

    [Fact]
    public async Task Inbound_derived_text_needs_approval()
    {
        var d = await Policy().EvaluateAsync(Request(inbound: true), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundReason.DerivedFromInboundMail, d.Reason);
    }

    [Fact]
    public async Task Attachment_needs_approval()
    {
        var d = await Policy().EvaluateAsync(Request(attachments: 1, attachmentBytes: 10), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundReason.Attachment, d.Reason);
    }

    [Fact]
    public async Task Sign_up_reply_needs_approval()
    {
        var d = await Policy().EvaluateAsync(Request(signUp: true), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundReason.SignUpReply, d.Reason);
    }

    [Fact]
    public async Task Bazaar_to_bazaar_is_refused()
    {
        var d = await Policy(directory: new FakeDirectory("a@mail.test", "b@mail.test")).EvaluateAsync(Request(to: ["a@mail.test", "B@mail.test"]), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.Refuse, d.Kind);
        Assert.Equal(OutboundReason.BazaarToBazaar, d.Reason);
    }

    [Fact]
    public async Task Mixed_bazaar_and_external_recipients_need_approval_not_refusal()
    {
        var d = await Policy(directory: new FakeDirectory("a@mail.test")).EvaluateAsync(Request(to: ["a@mail.test", "friend@example.org"]), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.NeedsOwnerApproval, d.Kind);
        Assert.Equal(OutboundReason.MixedBazaarRecipients, d.Reason);
    }

    [Fact]
    public async Task Bazaar_recipients_do_not_count_as_new_recipients()
    {
        var policy = Policy(new FakeSettings { NewRecipientsPerAgentDay = 1 }, new FakeDirectory("a@mail.test", "b@mail.test"));

        var d = await policy.EvaluateAsync(Request(to: ["a@mail.test", "b@mail.test", "friend@example.org"]), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.NeedsOwnerApproval, d.Kind);
    }

    [Fact]
    public async Task Install_total_is_shared_across_agents()
    {
        var policy = Policy(new FakeSettings { SentPerDay = 1 });

        var first = await policy.EvaluateAsync(Request(agent: "agent-1"), TestContext.Current.CancellationToken);
        var second = await policy.EvaluateAsync(Request(agent: "agent-2", nonce: "n2"), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.Send, first.Kind);
        Assert.Equal(OutboundReason.DailyQuota, second.Reason);
    }

    [Fact]
    public async Task Per_agent_sub_limit_pauses_one_agent_only()
    {
        var policy = Policy(new FakeSettings { SentPerAgentDay = 1 });

        await policy.EvaluateAsync(Request(agent: "agent-1"), TestContext.Current.CancellationToken);
        var again = await policy.EvaluateAsync(Request(agent: "agent-1", nonce: "n2"), TestContext.Current.CancellationToken);
        var other = await policy.EvaluateAsync(Request(agent: "agent-2", nonce: "n3"), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundReason.AgentDailyQuota, again.Reason);
        Assert.Equal(OutboundDecisionKind.Send, other.Kind);
    }

    [Fact]
    public async Task Agent_denial_takes_no_slot_from_the_shared_bucket()
    {
        // Agent 1 is denied by its own sub-limit. The agent check runs first, so the install total is untouched.
        var limiter = new InMemoryRateLimiter(TimeProvider.System);
        var settings = new FakeSettings { SentPerAgentDay = 1, SentPerDay = 2 };
        var policy = Policy(settings, limiter: limiter);

        await policy.EvaluateAsync(Request(agent: "agent-1"), TestContext.Current.CancellationToken);
        var denied = await policy.EvaluateAsync(Request(agent: "agent-1", nonce: "n2"), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundReason.AgentDailyQuota, denied.Reason);
        var probe = limiter.TryAcquire(OutboundMailPolicy.SendPolicy(settings), OutboundMailPolicy.InstallKey);
        Assert.True(probe.Allowed);
        Assert.Equal(0, probe.Remaining);
    }

    [Fact]
    public async Task Operator_contact_adds_a_line_and_keeps_the_ai_statement()
    {
        var settings = new FakeSettings { FooterWording = null, OperatorContact = "ops@example.org\nsecond line" };
        var d = await Policy(settings).EvaluateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.StartsWith(OutboundDefaults.Footer, d.Footer);
        Assert.EndsWith("Operator contact: ops@example.org second line", d.Footer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Blank_operator_contact_adds_no_line(string? contact)
    {
        var d = await Policy(new FakeSettings { FooterWording = null, OperatorContact = contact }).EvaluateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDefaults.Footer, d.Footer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("a@b@c")]
    [InlineData("Name <a@b.c>")]
    public async Task Bad_recipients_are_refused(string address)
    {
        var d = await Policy().EvaluateAsync(Request(to: [address]), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.Refuse, d.Kind);
    }

    [Fact]
    public async Task New_recipient_cap_breach_pauses()
    {
        var settings = new FakeSettings { NewRecipientsPerAgentDay = 2 };
        var policy = Policy(settings);

        var first = await policy.EvaluateAsync(Request(to: ["a@example.net", "b@example.net"]), TestContext.Current.CancellationToken);
        var second = await policy.EvaluateAsync(Request(to: ["c@example.net"], nonce: "n2"), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.NeedsOwnerApproval, first.Kind);
        Assert.Equal(OutboundDecisionKind.Pause, second.Kind);
        Assert.Equal(OutboundReason.NewRecipientCap, second.Reason);
        Assert.True(second.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task Cap_is_per_agent()
    {
        var policy = Policy(new FakeSettings { NewRecipientsPerAgentDay = 1 });

        await policy.EvaluateAsync(Request(to: ["a@example.net"], agent: "agent-1"), TestContext.Current.CancellationToken);
        var other = await policy.EvaluateAsync(Request(to: ["b@example.net"], agent: "agent-2"), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.NeedsOwnerApproval, other.Kind);
    }

    [Fact]
    public async Task Daily_total_breach_pauses_and_denied_send_is_not_counted_twice()
    {
        var policy = Policy(new FakeSettings { SentPerDay = 1 });

        var first = await policy.EvaluateAsync(Request(), TestContext.Current.CancellationToken);
        var second = await policy.EvaluateAsync(Request(nonce: "n2"), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.Send, first.Kind);
        Assert.Equal(OutboundDecisionKind.Pause, second.Kind);
        Assert.Equal(OutboundReason.DailyQuota, second.Reason);
    }

    [Fact]
    public async Task Refused_messages_consume_no_quota()
    {
        var policy = Policy(new FakeSettings { SentPerDay = 1 });

        await policy.EvaluateAsync(Request(to: []), TestContext.Current.CancellationToken);
        var d = await policy.EvaluateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.Send, d.Kind);
    }

    [Fact]
    public async Task Tightening_a_cap_to_zero_blocks_new_recipients()
    {
        var d = await Policy(new FakeSettings { NewRecipientsPerAgentDay = 0 }).EvaluateAsync(Request(to: ["x@example.net"]), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.Pause, d.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t")]
    public async Task Blank_footer_setting_falls_back_to_the_shipped_footer(string? wording)
    {
        var d = await Policy(new FakeSettings { FooterWording = wording }).EvaluateAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDefaults.Footer, d.Footer);
        Assert.False(string.IsNullOrWhiteSpace(d.Footer));
    }

    [Fact]
    public async Task Footer_is_never_empty_when_a_message_is_held_for_approval()
    {
        var d = await Policy(new FakeSettings { FooterWording = " " }).EvaluateAsync(Request(taint: MailTaint.Hard), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDefaults.Footer, d.Footer);
    }

    [Fact]
    public async Task Message_over_the_byte_limit_is_refused()
    {
        var d = await Policy(new FakeSettings { MaxMessageBytes = 100 }).EvaluateAsync(Request(body: new string('x', 200)), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundReason.TooLarge, d.Reason);
    }

    [Fact]
    public void Back_end_hard_cap_is_a_ceiling_for_the_byte_setting()
    {
        var settings = new FakeSettings { MaxMessageBytes = 100L * 1024 * 1024, BackendHardCapBytes = 25L * 1024 * 1024 };

        Assert.Equal(25L * 1024 * 1024, OutboundMailPolicy.EffectiveMaxBytes(settings));
        Assert.Equal(OutboundDefaults.MaxMessageBytes, OutboundMailPolicy.EffectiveMaxBytes(new FakeSettings { MaxMessageBytes = 0 }));
    }

    [Fact]
    public async Task Too_many_attachments_are_refused()
    {
        var d = await Policy(new FakeSettings { MaxAttachments = 1 }).EvaluateAsync(Request(attachments: 2, attachmentBytes: 5), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundReason.TooManyAttachments, d.Reason);
    }

    [Fact]
    public async Task Attachment_bytes_count_toward_the_size_limit()
    {
        var d = await Policy(new FakeSettings { MaxMessageBytes = 100 }).EvaluateAsync(Request(attachments: 1, attachmentBytes: 500), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundReason.TooLarge, d.Reason);
    }

    [Fact]
    public void Repeated_idempotency_inputs_yield_the_same_key()
    {
        var a = OutboundIdempotency.Derive(Request(to: ["B@example.org", "a@example.org"]));
        var b = OutboundIdempotency.Derive(Request(to: ["a@example.org", "b@EXAMPLE.org", "a@example.org"]));

        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void Key_changes_with_nonce_body_agent_and_recipients()
    {
        var baseKey = OutboundIdempotency.Derive(Request());

        Assert.NotEqual(baseKey, OutboundIdempotency.Derive(Request(nonce: "n2")));
        Assert.NotEqual(baseKey, OutboundIdempotency.Derive(Request(body: "other")));
        Assert.NotEqual(baseKey, OutboundIdempotency.Derive(Request(agent: "agent-2")));
        Assert.NotEqual(baseKey, OutboundIdempotency.Derive(Request(to: ["z@example.org"])));
    }

    [Fact]
    public async Task Decision_carries_the_derived_key()
    {
        var request = Request();
        var d = await Policy().EvaluateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(OutboundIdempotency.Derive(request), d.IdempotencyKey);
    }

    [Fact]
    public async Task Add_extension_registers_the_policy()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBazaarAddressDirectory>(new FakeDirectory());
        services.AddSingleton<IApprovedRecipients>(new FakeApproved());
        services.AddSingleton<IRateLimiter>(new InMemoryRateLimiter(TimeProvider.System));
        services.AddSingleton<IOutboundMailSettings>(new FakeSettings());
        services.AddBazaarMailOutbound();

        using var provider = services.BuildServiceProvider();
        var d = await provider.GetRequiredService<IOutboundMailPolicy>().EvaluateAsync(Request(to: ["n@example.net"]), TestContext.Current.CancellationToken);

        Assert.Equal(OutboundDecisionKind.NeedsOwnerApproval, d.Kind);
    }
}
