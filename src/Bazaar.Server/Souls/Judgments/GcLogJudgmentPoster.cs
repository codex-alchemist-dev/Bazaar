using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Souls.Judgments;

/// <summary>
/// Posts judgment messages through the GC log writer (docs/design/01). The body is sealed by <see cref="IGcBodyCodec"/> before
/// it reaches the writer, and the client message id is the decision id, so a retry returns the original event.
/// </summary>
public sealed class GcLogJudgmentPoster(IGcLogWriter writer, IGcBodyCodec bodyCodec) : IGcJudgmentPoster
{
    /// <summary>Prefix of the client message id that makes a judgment post idempotent.</summary>
    public const string ClientMsgIdPrefix = "judgment:";

    /// <inheritdoc />
    public async Task<JudgmentPostReceipt> PostAsync(JudgmentPost post, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);
        var request = new AppendRequest(
            post.GcId,
            GcEventKinds.Message,
            AuthorKind.Agent,
            post.AgentId,
            SourceTrust.Agent,
            ClientMsgIdPrefix + post.DecisionId.ToString("D"))
        {
            Message = bodyCodec.Seal(post.Text),
        };
        var result = await writer.AppendAsync(request, cancellationToken).ConfigureAwait(false);
        return new JudgmentPostReceipt(result.Seq, result.Duplicate);
    }
}
