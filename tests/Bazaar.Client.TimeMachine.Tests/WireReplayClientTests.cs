using Bazaar.Client.Core.Wire;
using Bazaar.Client.TimeMachine;
using Bazaar.Contracts.TimeMachine;
using Bazaar.Contracts.Wire;
using WireForkRequest = Bazaar.Contracts.TimeMachine.ReplayForkRequest;

namespace Bazaar.Client.TimeMachine.Tests;

public class WireReplayClientTests
{
    private sealed class FakeRequester : IWireRequester
    {
        public List<(string Type, object Payload)> Sent { get; } = [];

        public Func<string, object, object>? Answer { get; set; }

        public WireError? Error { get; set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload!));
            if (Error is { } error)
            {
                throw new WireRequestException(error);
            }

            return Task.FromResult((TResponse)Answer!(type, payload!));
        }
    }

    private static readonly Guid _gc = Guid.NewGuid();

    private static ReplayFrameResult Result(long seq, long head, string? payload) => new(
        _gc, seq, head,
        new ReplayFrameWire(
            [new ReplayMessageWire(Guid.NewGuid(), Guid.NewGuid(), 1, 1, 1, false, true, payload, DateTimeOffset.UnixEpoch)],
            new ReplayPartWire<IReadOnlyList<ReplayTaskWire>>(true, [new ReplayTaskWire(Guid.NewGuid(), "t", "Review")], null),
            new ReplayPartWire<ReplayVaultWire>(false, null, "Vault module not installed"),
            new ReplayPartWire<IReadOnlyList<ReplaySoulWire>>(true, [], null),
            new ReplayPartWire<IReadOnlyList<ReplayReputationWire>>(false, null, null)));

    [Fact]
    public async Task Frame_maps_to_display_models_and_reports_the_clamp()
    {
        var requester = new FakeRequester { Answer = (_, _) => Result(seq: 4, head: 4, "{\"text\":\"hello\"}") };
        var client = new WireReplayClient(requester);

        var view = await client.GetFrameAsync(_gc, 10, CancellationToken.None);

        var sent = Assert.Single(requester.Sent);
        Assert.Equal("replay.frame", sent.Type);
        Assert.Equal(new ReplayFrameRequest(_gc, 10), sent.Payload);
        Assert.Equal(4, view.EffectiveSeq);
        Assert.Equal(4, view.HeadSeq);
        Assert.True(view.Clamped);
        var message = Assert.Single(view.Messages);
        Assert.Equal("hello", message.Text);
        Assert.True(message.Quarantined);
        Assert.Equal("Kanban: 1 task(s)", view.Parts[0].Line);
        Assert.Equal("Vault: unavailable (Vault module not installed)", view.Parts[1].Line);
        Assert.True(view.Parts[2].IsAvailable);
        Assert.False(view.Parts[3].IsAvailable);
    }

    [Fact]
    public async Task Frame_not_clamped_when_the_server_renders_the_asked_seq()
    {
        var client = new WireReplayClient(new FakeRequester { Answer = (_, _) => Result(seq: 3, head: 9, null) });

        var view = await client.GetFrameAsync(_gc, 3, CancellationToken.None);

        Assert.False(view.Clamped);
        Assert.Null(view.Messages[0].Text);
    }

    [Theory]
    [InlineData("{\"kind\":\"image\"}", "{\"kind\":\"image\"}")]
    [InlineData("plain words", "plain words")]
    public async Task Payload_without_a_text_field_is_shown_as_is(string payload, string expected)
    {
        var client = new WireReplayClient(new FakeRequester { Answer = (_, _) => Result(1, 1, payload) });

        var view = await client.GetFrameAsync(_gc, 1, CancellationToken.None);

        Assert.Equal(expected, view.Messages[0].Text);
    }

    [Fact]
    public async Task Fork_sends_the_request_and_maps_the_reply()
    {
        var newGc = Guid.NewGuid();
        var requester = new FakeRequester { Answer = (_, _) => new ReplayForkResult(newGc, "abc", Created: false) };
        var client = new WireReplayClient(requester);
        var request = new ReplayForkRequest(Guid.NewGuid(), _gc, 7);

        var outcome = await client.ForkAsync(request, CancellationToken.None);

        var sent = Assert.Single(requester.Sent);
        Assert.Equal("replay.fork", sent.Type);
        Assert.Equal(new WireForkRequest(_gc, 7, request.RequestId), sent.Payload);
        Assert.Equal(newGc, outcome.NewGcId);
        Assert.False(outcome.Created);
        Assert.Equal(7, outcome.SourceSeq);
    }

    [Theory]
    [InlineData(WireErrorCodes.NotFound, "not available")]
    [InlineData(WireErrorCodes.RateLimited, "Too many")]
    [InlineData(WireErrorCodes.Internal, "could not serve")]
    public async Task Server_errors_become_safe_client_exceptions(string code, string fragment)
    {
        var client = new WireReplayClient(new FakeRequester { Error = new WireError(code, "secret detail", null) });

        var frame = await Assert.ThrowsAsync<ReplayClientException>(() => client.GetFrameAsync(_gc, 1, CancellationToken.None));
        var fork = await Assert.ThrowsAsync<ReplayClientException>(() => client.ForkAsync(new ReplayForkRequest(Guid.NewGuid(), _gc, 1), CancellationToken.None));

        Assert.Contains(fragment, frame.Message);
        Assert.Contains(fragment, fork.Message);
        Assert.DoesNotContain("secret", frame.Message);
    }
}
