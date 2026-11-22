using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;
using Bazaar.Souls.Abstractions;

namespace Bazaar.Server.ThinkingGc;

/// <summary>
/// Writes a turn's thinking and steps into the agent's Thinking GC (doc 01 Thinking GCs; doc 06 Thinking GCs). Each entry is one
/// ordinary message event authored by the agent, so the log's rules hold: append-only, edits keep every version, and nothing is
/// ever purged. Text longer than the message limit is split into consecutive messages, never cut. The body is sealed under the
/// agent's own key, like any agent post.
/// </summary>
/// <remarks>
/// The client message id is <c>think:{turn}:{ordinal}:{kind}:{chunk}</c>, so a repeated entry (a recovered turn, a retry) is a
/// no-op. The Thinking GC is never a mention or wake target and is never loaded into another agent's context (those rules live
/// in the GC module and the context builder); this class only writes.
/// </remarks>
public sealed class ThinkingGcRecorder(
    ThinkingGcStore store,
    IGcLogWriter writer,
    IGcBodyCodec bodyCodec,
    ISoulBindings? souls = null) : IThinkingRecorder
{
    /// <inheritdoc />
    public async Task<bool> RecordAsync(ThinkingEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Text);
        ArgumentOutOfRangeException.ThrowIfLessThan(entry.Ordinal, 1);
        if (!Guid.TryParse(entry.WorkingGcId, out var workingGcId))
            return false;

        var thinkingGcId = await store.EnsureAsync(workingGcId, entry.AgentId, cancellationToken).ConfigureAwait(false);
        if (thinkingGcId is null)
            return false;

        var soul = souls is null ? null : await souls.ResolveAsync(entry.AgentId, cancellationToken).ConfigureAwait(false);
        var kind = entry.Kind.ToString().ToLowerInvariant();
        var chunks = Split(entry.Text, GcLimits.MaxTextLength);
        for (var i = 0; i < chunks.Count; i++)
        {
            var body = await bodyCodec.SealAsync(entry.AgentId, chunks[i], cancellationToken).ConfigureAwait(false);
            var request = new AppendRequest(
                thinkingGcId.Value,
                GcEventKinds.Message,
                AuthorKind.Agent,
                entry.AgentId,
                SourceTrust.Agent,
                $"think:{entry.TurnId:N}:{entry.Ordinal}:{kind}:{i}")
            {
                Message = body,
                Soul = soul,
            };
            await writer.AppendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Splits text into pieces of at most <paramref name="max"/> characters without cutting a surrogate pair.</summary>
    public static List<string> Split(string text, int max)
    {
        var parts = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(max, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
                length--;
            parts.Add(text.Substring(start, length));
            start += length;
        }

        return parts;
    }
}
