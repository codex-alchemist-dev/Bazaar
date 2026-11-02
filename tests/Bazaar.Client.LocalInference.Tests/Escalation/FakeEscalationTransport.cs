using Bazaar.Client.LocalInference.Escalation;

namespace Bazaar.Client.LocalInference.Tests.Escalation;

/// <summary>Scripted transport for tests. Records every request it receives; no network is used.</summary>
public sealed class FakeEscalationTransport : IEscalationTransport
{
    private readonly Queue<Func<EscalationRequest, EscalationReply>> _script = new();

    public List<EscalationRequest> Requests { get; } = [];

    public int CallCount => Requests.Count;

    public FakeEscalationTransport Reply(string text) =>
        Add(_ => new EscalationReply(text, EscalationTier.Cloud, null));

    public FakeEscalationTransport ReplyWithError(string error) =>
        Add(_ => new EscalationReply(string.Empty, EscalationTier.Cloud, error));

    public FakeEscalationTransport Throw(Exception error) =>
        Add(_ => throw error);

    public Task<EscalationReply> SendAsync(EscalationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(_script.Count == 0
            ? new EscalationReply("default", EscalationTier.Cloud, null)
            : _script.Dequeue()(request));
    }

    private FakeEscalationTransport Add(Func<EscalationRequest, EscalationReply> step)
    {
        _script.Enqueue(step);
        return this;
    }
}
