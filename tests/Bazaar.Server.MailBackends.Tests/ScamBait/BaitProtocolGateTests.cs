using System.Globalization;
using Bazaar.Server.Mail;
using Bazaar.Server.MailBackends.ScamBait;

namespace Bazaar.Server.MailBackends.Tests.ScamBait;

public sealed class BaitProtocolGateTests
{

    private static readonly BaitProtocolGate.BaitSettings _on =
        new(ProtocolEnabled: true, AcknowledgedAt: DateTimeOffset.Parse("2026-10-30T00:00:00Z", CultureInfo.InvariantCulture));

    private static BaitProtocolGate.ReplyRequest Persona(bool approved = true, string? reason = "reply to a fee request") =>
        new(new MailboxRef("example.org", "persona-01"), IsPersonaMailbox: true, IsAgentMailbox: false,
            OwnerApproved: approved, OwnerReason: reason, Signals: new HashSet<BaitProtocolGate.StopSignal>());

    [Fact]
    public void Off_by_default_so_every_reply_is_disabled()
    {
        Assert.Equal(BaitProtocolGate.Verdict.Disabled, BaitProtocolGate.Evaluate(BaitProtocolGate.BaitSettings.Default, Persona()));
    }

    [Fact]
    public void Enabled_without_an_acknowledgement_is_refused()
    {
        var settings = new BaitProtocolGate.BaitSettings(ProtocolEnabled: true, AcknowledgedAt: null);
        Assert.Equal(BaitProtocolGate.Verdict.NotAcknowledged, BaitProtocolGate.Evaluate(settings, Persona()));
    }

    [Fact]
    public void Agent_mailbox_is_never_used_as_bait()
    {
        var request = Persona() with { IsAgentMailbox = true, IsPersonaMailbox = true };
        Assert.Equal(BaitProtocolGate.Verdict.AgentMailboxForbidden, BaitProtocolGate.Evaluate(_on, request));
    }

    [Fact]
    public void Sender_must_be_a_marked_persona()
    {
        var request = Persona() with { IsPersonaMailbox = false };
        Assert.Equal(BaitProtocolGate.Verdict.PersonaRequired, BaitProtocolGate.Evaluate(_on, request));
    }

    [Theory]
    [InlineData(BaitProtocolGate.StopSignal.RealThirdPartyDetected)]
    [InlineData(BaitProtocolGate.StopSignal.Threat)]
    [InlineData(BaitProtocolGate.StopSignal.CredentialRequest)]
    [InlineData(BaitProtocolGate.StopSignal.MoneyRequest)]
    public void Any_stop_signal_stops_even_when_approved(BaitProtocolGate.StopSignal signal)
    {
        var request = Persona() with { Signals = new HashSet<BaitProtocolGate.StopSignal> { signal } };
        var verdict = BaitProtocolGate.Evaluate(_on, request);
        Assert.Equal(BaitProtocolGate.Verdict.StopTriggered, verdict);
        Assert.True(BaitProtocolGate.MustDisable(verdict));
    }

    [Fact]
    public void Missing_owner_approval_or_reason_is_refused()
    {
        Assert.Equal(BaitProtocolGate.Verdict.OwnerApprovalRequired, BaitProtocolGate.Evaluate(_on, Persona(approved: false)));
        Assert.Equal(BaitProtocolGate.Verdict.OwnerApprovalRequired, BaitProtocolGate.Evaluate(_on, Persona(reason: "  ")));
    }

    [Fact]
    public void Approved_persona_reply_is_allowed_and_does_not_disable()
    {
        var verdict = BaitProtocolGate.Evaluate(_on, Persona());
        Assert.Equal(BaitProtocolGate.Verdict.Allowed, verdict);
        Assert.False(BaitProtocolGate.MustDisable(verdict));
    }
}
