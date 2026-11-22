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
    ISoulBindings? souls = null,
    IThinkingPointerPolicy? pointerPolicy = null) : IThinkingRecorder
{
    /// <summary>The system author id of the pointer event.</summary>
    public static readonly Guid SystemAuthorId = Guid.Parse("00000000-0000-7000-8000-000000000001");

    /// <inheritdoc />
    public async Task<bool> RecordAsync(ThinkingEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Text);
        ArgumentOutOfRangeException.ThrowIfLessThan(entry.Ordinal, 1);
        if (!Guid.TryParse(entry.WorkingGcId, out var workingGcId))
            return false;

        var ensured = await store.EnsureWithStatusAsync(workingGcId, entry.AgentId, cancellationToken).ConfigureAwait(false);
        if (ensured is not var (id, created))
            return false;
        var thinkingGcId = (Guid?)id;

        if (created && pointerPolicy is not null && await pointerPolicy.IsEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            // One collapsed pointer in the parent (doc 01 [PROPOSED]). It carries ids only, never thinking text. Idempotent per Thinking GC.
            var json = "{\"kind\":\"thinking_pointer\",\"thinking_gc_id\":\"" + id.ToString("D") + "\",\"agent_id\":\"" + entry.AgentId.ToString("D") + "\",\"collapsed\":true}";
            await writer.AppendAsync(
                new AppendRequest(workingGcId, GcEventKinds.System, AuthorKind.System, SystemAuthorId, SourceTrust.System, $"thinking-pointer:{id:N}")
                {
                    PayloadJson = json,
                },
                cancellationToken).ConfigureAwait(false);
        }

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
