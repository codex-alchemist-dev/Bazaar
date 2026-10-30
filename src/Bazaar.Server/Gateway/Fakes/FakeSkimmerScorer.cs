using System.Text;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Ai;

namespace Bazaar.Server.Gateway.Fakes;

/// <summary>
/// A scripted Skimmer scorer (docs/design/13-skimmer-bouncer.md). It uses the same seam as every other model call: its
/// scores are scripted <see cref="ModelEvent"/> steps on a <see cref="FakeProvider"/>, not a second fake. Scorer output is
/// the raw text the model streamed; parsing the closed schema is the scorer's job, not this fake's.
/// </summary>
public sealed class FakeSkimmerScorer
{
    private readonly FakeProvider _provider;
    private readonly FakeModelGateway _gateway;

    public FakeSkimmerScorer(FakeProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _gateway = new FakeModelGateway(provider);
    }

    /// <summary>Every scorer request that reached the seam, in order.</summary>
    public IReadOnlyList<ModelRequest> Calls => _provider.Calls;

    /// <summary>Queues one scored answer: the text the scorer model streams, then a clean finish.</summary>
    public void EnqueueScore(string output, string servedModel = "claude-haiku-5-5")
    {
        ArgumentNullException.ThrowIfNull(output);
        _provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta(output)),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, servedModel)));
    }

    /// <summary>Queues any scripted steps on the same seam, such as a 429 or a timeout.</summary>
    public void Enqueue(params ScriptStep[] steps) => _provider.EnqueueCall(steps);

    /// <summary>
    /// Plays the next scripted call and returns its text. A failure event throws <see cref="GatewayModelException"/>, which
    /// the Skimmer treats as "rules stand". A stream that ends without a done event also throws.
    /// </summary>
    public async Task<ScorerReply> ScoreAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = new StringBuilder();
        string? servedModel = null;

        await foreach (var modelEvent in _gateway.CallAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (modelEvent)
            {
                case ModelEvent.TextDelta delta:
                    text.Append(delta.Text);
                    break;

                case ModelEvent.Failure failure:
                    throw new GatewayModelException(failure.Code, failure.Message, failure.RetryAfterSeconds);

                case ModelEvent.Done done:
                    servedModel = done.ServedModel;
                    break;
            }
        }

        if (servedModel is null)
        {
            throw new GatewayModelException("stream_dropped", "The scorer stream ended without a done event.");
        }

        return new ScorerReply(text.ToString(), servedModel);
    }
}

/// <summary>The scorer's streamed text and the model the upstream says served it.</summary>
public sealed record ScorerReply(string Text, string ServedModel);
