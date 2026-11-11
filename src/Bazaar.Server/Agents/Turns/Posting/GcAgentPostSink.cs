using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Wire;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Agents.Turns.Posting;

/// <summary>
/// Posts an agent's final text into a GC through the GC log writer (docs/design/06 "Turn loop" step 5). The message lands in the
/// log like any other message: author kind agent, source trust agent (soft taint, DECISIONS-LOG round 2), body sealed by
/// <see cref="IGcBodyCodec"/>.
/// </summary>
/// <remarks>
/// The client message id is <c>turn:{turnId}:{sha256(text)}</c>. The writer is idempotent by (GC, author, client message id), so a
/// retry of the same turn with the same text returns the original event and writes nothing. Keying on the text as well as the
/// turn means a second, different post in the same turn is not silently swallowed as a duplicate.
/// </remarks>
/// <remarks>
/// INTERIM: this path calls <see cref="IGcLogWriter"/> directly. Mention parsing and loop guards are NOT applied here; the
/// higher-level GC post service that does them does not exist yet (the wire <c>gc.send</c> handler takes client-parsed mentions
/// and a session principal, so it cannot serve an agent post). No mentions are attached.
/// </remarks>
public sealed class GcAgentPostSink(IGcLogWriter writer, IGcBodyCodec bodyCodec) : IAgentPostSink
{
    /// <summary>Prefix of the client message id that makes an agent post idempotent per turn.</summary>
    public const string ClientMsgIdPrefix = "turn:";

    /// <inheritdoc />
    public async Task PostAsync(Guid agentId, Guid turnId, string gcId, string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gcId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > GcSendHandler.MaxTextLength)
            throw new ArgumentException($"Agent post text is longer than {GcSendHandler.MaxTextLength} characters.", nameof(text));
        if (!Guid.TryParse(gcId, out var gcGuid))
            throw new ArgumentException("The GC id must be a GUID.", nameof(gcId));

        var body = await bodyCodec.SealAsync(agentId, text, cancellationToken).ConfigureAwait(false);
        var request = new AppendRequest(
            gcGuid,
            GcEventKinds.Message,
            AuthorKind.Agent,
            agentId,
            SourceTrust.Agent,
            ClientMsgId(turnId, text))
        {
            Message = body,
        };
        await writer.AppendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The idempotency key for one turn's post of <paramref name="text"/>.</summary>
    public static string ClientMsgId(Guid turnId, string text)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return $"{ClientMsgIdPrefix}{turnId:D}:{hash}";
    }
}
