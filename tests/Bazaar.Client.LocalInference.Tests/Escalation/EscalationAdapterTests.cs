using Bazaar.Client.LocalInference.Escalation;

namespace Bazaar.Client.LocalInference.Tests.Escalation;

public class EscalationAdapterTests
{
    private const string SecretInput = "private thread text";

    private static EscalationRequest Request(string input = SecretInput) =>
        new(LocalTaskKind.ChipSummary, input, 256, "corr-1");

    private static EscalationAdapter Adapter(FakeEscalationTransport transport, EscalationMode mode) =>
        new(transport, new EscalationOptions { Mode = mode });

    [Fact]
    public void Default_mode_is_Explicit()
    {
        Assert.Equal(EscalationMode.Explicit, new EscalationOptions().Mode);
        Assert.Equal(EscalationMode.Explicit, EscalationOptions.DefaultMode);
    }

    [Fact]
    public async Task Default_options_return_a_pending_decision_without_calling_transport()
    {
        var transport = new FakeEscalationTransport();
        var adapter = new EscalationAdapter(transport, new EscalationOptions());

        var outcome = await adapter.RequestAsync(Request(), CancellationToken.None);

        Assert.IsType<EscalationPending>(outcome);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Off_refuses_and_transport_is_not_called()
    {
        var transport = new FakeEscalationTransport().Reply("should not be sent");
        var adapter = Adapter(transport, EscalationMode.Off);

        var outcome = await adapter.RequestAsync(Request(), CancellationToken.None);

        Assert.Equal(new EscalationRefused(EscalationRefusalReason.Disabled), outcome);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Off_refuses_confirmation_and_transport_is_not_called()
    {
        var transport = new FakeEscalationTransport().Reply("should not be sent");
        var adapter = Adapter(transport, EscalationMode.Off);

        var outcome = await adapter.ConfirmAsync(EscalationConfirmationToken.New(), CancellationToken.None);

        Assert.Equal(new EscalationRefused(EscalationRefusalReason.Disabled), outcome);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Explicit_without_confirmation_does_not_call_transport()
    {
        var transport = new FakeEscalationTransport().Reply("should not be sent");
        var adapter = Adapter(transport, EscalationMode.Explicit);

        var outcome = await adapter.RequestAsync(Request(), CancellationToken.None);

        var pending = Assert.IsType<EscalationPending>(outcome);
        Assert.Equal(Request(), pending.Decision.Request);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Explicit_with_confirmation_calls_transport_once()
    {
        var transport = new FakeEscalationTransport().Reply("summary");
        var adapter = Adapter(transport, EscalationMode.Explicit);
        var pending = Assert.IsType<EscalationPending>(await adapter.RequestAsync(Request(), CancellationToken.None));

        var outcome = await adapter.ConfirmAsync(pending.Decision.Token, CancellationToken.None);

        var sent = Assert.IsType<EscalationSent>(outcome);
        Assert.Equal("summary", sent.Reply.Text);
        Assert.Equal(EscalationTier.Cloud, sent.Reply.Tier);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal(Request(), transport.Requests[0]);
    }

    [Fact]
    public async Task Explicit_confirmation_is_single_use()
    {
        var transport = new FakeEscalationTransport().Reply("summary");
        var adapter = Adapter(transport, EscalationMode.Explicit);
        var pending = Assert.IsType<EscalationPending>(await adapter.RequestAsync(Request(), CancellationToken.None));
        await adapter.ConfirmAsync(pending.Decision.Token, CancellationToken.None);

        var second = await adapter.ConfirmAsync(pending.Decision.Token, CancellationToken.None);

        Assert.Equal(new EscalationRefused(EscalationRefusalReason.UnknownConfirmation), second);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Explicit_unknown_token_is_refused_and_transport_is_not_called()
    {
        var transport = new FakeEscalationTransport().Reply("should not be sent");
        var adapter = Adapter(transport, EscalationMode.Explicit);

        var outcome = await adapter.ConfirmAsync(EscalationConfirmationToken.New(), CancellationToken.None);

        Assert.Equal(new EscalationRefused(EscalationRefusalReason.UnknownConfirmation), outcome);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Confirmation_sends_the_request_shown_to_the_user_not_a_later_one()
    {
        var transport = new FakeEscalationTransport().Reply("summary");
        var adapter = Adapter(transport, EscalationMode.Explicit);
        var pending = Assert.IsType<EscalationPending>(await adapter.RequestAsync(Request("first"), CancellationToken.None));

        await adapter.ConfirmAsync(pending.Decision.Token, CancellationToken.None);

        Assert.Equal(Request("first"), transport.Requests.Single());
    }

    [Fact]
    public async Task Automatic_calls_transport_without_confirmation()
    {
        var transport = new FakeEscalationTransport().Reply("summary");
        var adapter = Adapter(transport, EscalationMode.Automatic);

        var outcome = await adapter.RequestAsync(Request(), CancellationToken.None);

        var sent = Assert.IsType<EscalationSent>(outcome);
        Assert.Equal("summary", sent.Reply.Text);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task Transport_error_is_returned_as_typed_failure()
    {
        var transport = new FakeEscalationTransport().ReplyWithError("gateway unavailable");
        var adapter = Adapter(transport, EscalationMode.Automatic);

        var outcome = await adapter.RequestAsync(Request(), CancellationToken.None);

        var failed = Assert.IsType<EscalationFailed>(outcome);
        Assert.Equal(EscalationFailureKind.TransportReturnedError, failed.Kind);
        Assert.Equal("gateway unavailable", failed.Message);
    }

    [Fact]
    public async Task Transport_exception_is_returned_as_typed_failure_without_input_or_message()
    {
        var transport = new FakeEscalationTransport().Throw(new InvalidOperationException($"failed on {SecretInput}"));
        var adapter = Adapter(transport, EscalationMode.Automatic);

        var outcome = await adapter.RequestAsync(Request(), CancellationToken.None);

        var failed = Assert.IsType<EscalationFailed>(outcome);
        Assert.Equal(EscalationFailureKind.TransportThrew, failed.Kind);
        Assert.Equal(nameof(InvalidOperationException), failed.Message);
        Assert.DoesNotContain(SecretInput, failed.Message);
    }

    [Fact]
    public async Task Cancellation_propagates_and_is_not_a_failure()
    {
        var transport = new FakeEscalationTransport().Throw(new OperationCanceledException());
        var adapter = Adapter(transport, EscalationMode.Automatic);

        await Assert.ThrowsAsync<OperationCanceledException>(() => adapter.RequestAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task ContentLeavesDevice_is_true_whenever_transport_is_called()
    {
        var automatic = new FakeEscalationTransport().Reply("ok").Throw(new TimeoutException()).ReplyWithError("bad");
        var automaticAdapter = Adapter(automatic, EscalationMode.Automatic);

        var sent = Assert.IsType<EscalationSent>(await automaticAdapter.RequestAsync(Request(), CancellationToken.None));
        var threw = Assert.IsType<EscalationFailed>(await automaticAdapter.RequestAsync(Request(), CancellationToken.None));
        var errored = Assert.IsType<EscalationFailed>(await automaticAdapter.RequestAsync(Request(), CancellationToken.None));

        Assert.True(sent.ContentLeavesDevice);
        Assert.True(threw.ContentLeavesDevice);
        Assert.True(errored.ContentLeavesDevice);
        Assert.Equal(3, automatic.CallCount);

        var explicitTransport = new FakeEscalationTransport().Reply("ok");
        var explicitAdapter = Adapter(explicitTransport, EscalationMode.Explicit);
        var pending = Assert.IsType<EscalationPending>(await explicitAdapter.RequestAsync(Request(), CancellationToken.None));
        Assert.True(pending.Decision.ContentLeavesDevice);

        var confirmed = Assert.IsType<EscalationSent>(await explicitAdapter.ConfirmAsync(pending.Decision.Token, CancellationToken.None));
        Assert.True(confirmed.ContentLeavesDevice);
        Assert.Equal(1, explicitTransport.CallCount);
    }

    [Fact]
    public void Unknown_mode_is_rejected_at_construction()
    {
        var options = new EscalationOptions { Mode = (EscalationMode)99 };

        Assert.Throws<ArgumentOutOfRangeException>(() => new EscalationAdapter(new FakeEscalationTransport(), options));
    }
}
