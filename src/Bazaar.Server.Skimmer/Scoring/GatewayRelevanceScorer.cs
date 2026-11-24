using System.Text;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway;

namespace Bazaar.Server.Skimmer.Scoring;

/// <summary>
/// The real <see cref="IRelevanceScorer"/>: one gateway call per request (doc 13, "Security"). The call is a reader session:
/// a single user message, no tools. The reply must pass <see cref="ScorerOutputParser"/>, or the scorer throws so the gate
/// takes its fallback path. No free text from the model reaches anyone.
/// </summary>
/// <param name="gateway">The model gateway (doc 05).</param>
/// <param name="gatekeeperId">The component identity the usage is recorded against. It has no Soul, memory or tools.</param>
/// <param name="route">The model route, the value of <c>skimmer.scorer_model</c>.</param>
public sealed class GatewayRelevanceScorer(IModelGateway gateway, string gatekeeperId, string route) : IRelevanceScorer
{
    private const int _maxOutputTokens = 400;

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelevanceResult>> ScoreAsync(ScoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = new ModelRequest(
            gatekeeperId,
            route,
            [new ModelMessage(ModelRole.User, ScorePromptBuilder.Build(request))],
            [],
            MaxOutputTokens: _maxOutputTokens,
            Temperature: 0);

        var text = new StringBuilder();
        await foreach (var ev in gateway.CallAsync(model, ct).ConfigureAwait(false))
        {
            switch (ev)
            {
                case ModelEvent.TextDelta delta:
                    text.Append(delta.Text);
                    break;
                case ModelEvent.Failure failure:
                    throw new InvalidOperationException($"Scorer gateway call failed: {failure.Code}.");
            }
        }

        var parsed = ScorerOutputParser.Parse(text.ToString(), request.Agents.Select(a => a.AgentId).ToList());
        return parsed.Success
            ? parsed.Results
            : throw new InvalidOperationException($"Scorer output rejected: {parsed.Reason}.");
    }
}
