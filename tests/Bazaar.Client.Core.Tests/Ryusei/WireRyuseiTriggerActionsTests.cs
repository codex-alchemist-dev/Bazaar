using Bazaar.Client.Core.Ryusei;
using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.Ryusei;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Tests.Ryusei;

public sealed class WireRyuseiTriggerActionsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records each request and answers pause and resume, or throws when told to refuse.</summary>
    private sealed class FakeRequester : IWireRequester
    {
        public List<(string Type, object Payload)> Sent { get; } = [];
        public Exception? Refusal { get; set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            Sent.Add((type, payload!));
            if (Refusal is not null)
            {
                throw Refusal;
            }

            var reply = new RyuseiTriggerChangeReply(((RyuseiTriggerChangeRequest)(object)payload!).TriggerId, 2);
            return Task.FromResult((TResponse)(object)reply);
        }
    }

    [Fact]
    public async Task Pause_sends_the_pause_wire_type_with_the_trigger_id()
    {
        var requests = new FakeRequester();
        var id = Guid.CreateVersion7();

        await new WireRyuseiTriggerActions(requests).PauseAsync(id.ToString("D"), Ct);

        var sent = Assert.Single(requests.Sent);
        Assert.Equal(WireTypes.RyuseiPause, sent.Type);
        Assert.Equal(id, ((RyuseiTriggerChangeRequest)sent.Payload).TriggerId);
    }

    [Fact]
    public async Task Resume_sends_the_resume_wire_type_with_the_trigger_id()
    {
        var requests = new FakeRequester();
        var id = Guid.CreateVersion7();

        await new WireRyuseiTriggerActions(requests).ResumeAsync(id.ToString("D"), Ct);

        var sent = Assert.Single(requests.Sent);
        Assert.Equal(WireTypes.RyuseiResume, sent.Type);
        Assert.Equal(id, ((RyuseiTriggerChangeRequest)sent.Payload).TriggerId);
    }

    [Fact]
    public async Task A_malformed_trigger_id_is_rejected_before_anything_is_sent()
    {
        var requests = new FakeRequester();

        await Assert.ThrowsAsync<ArgumentException>(() => new WireRyuseiTriggerActions(requests).PauseAsync("not-a-guid", Ct));

        Assert.Empty(requests.Sent);
    }

    [Fact]
    public async Task A_refusal_from_the_server_reaches_the_caller()
    {
        var requests = new FakeRequester { Refusal = new InvalidOperationException("refused") };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WireRyuseiTriggerActions(requests).ResumeAsync(Guid.CreateVersion7().ToString("D"), Ct));
    }
}
